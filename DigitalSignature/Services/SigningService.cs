using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using DigitalSignature.Models;
using SPMS;

namespace DigitalSignature.Services;

// Extracted verbatim from DGSignController.cs (see git history for the
// original inline versions) — pure relocation, no behavior changes. Shared
// by DGSignController (JWT-authenticated) and PublicSignController
// (token-authenticated).
public class SigningService : ISigningService
{
    private readonly IDatabaseService _dbService;
    private readonly ISecureCredentialService _credentialService;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<SigningService> _logger;
    private readonly IWebHostEnvironment _env;

    private string NetworkPath => _fileStorage.RootPath;
    private string DigitalSignaturePath => _fileStorage.DigitalSignaturePath;
    private string DigitalSpicimenPath => _fileStorage.DigitalSpicimenPath;

    public SigningService(
        IDatabaseService dbService,
        ISecureCredentialService credentialService,
        IFileStorageService fileStorage,
        ILogger<SigningService> logger,
        IWebHostEnvironment env)
    {
        _dbService = dbService;
        _credentialService = credentialService;
        _fileStorage = fileStorage;
        _logger = logger;
        _env = env;
    }

    // ==================== GET PENDING DOCUMENTS ====================

    public async Task<List<PendingDocumentDto>> GetPendingDocumentsAsync(int year, int eid, int userType, int? docTypeId)
    {
        // ── Query 1: Get core pending documents (no "last action" lookup) ──
        var sqlCore = @"
            DECLARE @YearStart DATETIME = DATEFROMPARTS(@Year, 1, 1);
            DECLARE @YearEnd   DATETIME = DATEADD(YEAR, 1, @YearStart);

            SELECT
                da.doc_id           AS DocId,
                da.doc_name         AS DocName,
                da.doc_code         AS DocCode,
                da.doc_description  AS DocDescription,
                da.doc_datetime     AS DocCreatedDatetime,
                da.doc_eid          AS DocEid,
                da.doc_eid_user_type AS DocEidUserType,
                da.doc_type_id      AS DocTypeId,
                da.doc_status_id    AS DocStatusId,
                rs.status_type      AS DocStatusName,
                dt.document_description AS DocTypeName,
                dt.document_abbr    AS DocTypeAbbr,
                ds.sig_id           AS SigId,
                ds.sig_order        AS SigOrder,
                ds.sig_eid          AS SigEid,
                ds.sig_user_type    AS SigUserType,
                ds.sig_status       AS SigStatus,
                rss.description     AS SigStatusName,
                ds.date_time_inserted AS SignatoryAssignedDatetime,
                CAST(NULL AS DATETIME) AS CurrentSignatureDatetime,
                ISNULL(ds.sig_level, 0) AS SigLevel,
                ISNULL(ds.sig_sign_count, 0) AS SigSignCount
            FROM [bacpdfsign].[dbo].[document_signatories] ds
            INNER JOIN [bacpdfsign].[dbo].[document_attach] da
                ON ds.doc_id = da.doc_id
            INNER JOIN [bacpdfsign].[dbo].[document_types] dt
                ON da.doc_type_id = dt.id
            INNER JOIN [bacpdfsign].[dbo].[req_status] rs
                ON da.doc_status_id = rs.id
            INNER JOIN [bacpdfsign].[dbo].[req_sign_status] rss
                ON ds.sig_status = rss.id
            WHERE ds.sig_eid = @Eid
              AND ds.sig_user_type = @UserType
              AND ds.sig_status = 0
              AND da.doc_status_id IN (1, 3)
              AND CASE WHEN ISDATE(da.doc_datetime) = 1 THEN CONVERT(datetime, da.doc_datetime, 100) END >= @YearStart
              AND CASE WHEN ISDATE(da.doc_datetime) = 1 THEN CONVERT(datetime, da.doc_datetime, 100) END <  @YearEnd
              AND (@DocTypeId IS NULL OR da.doc_type_id = @DocTypeId)
              AND NOT EXISTS (
                  SELECT 1
                  FROM [bacpdfsign].[dbo].[document_signatories] ds2
                  WHERE ds2.doc_id = ds.doc_id
                    AND ds2.sig_order < ds.sig_order
                    AND ds2.sig_status != 1
              )
            ORDER BY CASE WHEN ISDATE(da.doc_datetime) = 1 THEN CONVERT(datetime, da.doc_datetime, 100) END DESC";

        var coreResults = (await _dbService.QueryAsync<PendingDocumentCoreDto, dynamic>(
            sqlCore,
            new { Year = year, Eid = eid, UserType = userType, DocTypeId = docTypeId },
            CommandType.Text)).ToList();

        if (coreResults.Count == 0)
            return new List<PendingDocumentDto>();

        // Collect distinct lookup keys
        var docIds = coreResults.Select(r => r.DocId).Distinct().ToList();

        // Collect all unique (eid, user_type) pairs for name resolution
        var namePairs = coreResults
            .SelectMany(r => new[]
            {
                (Eid: r.DocEid, UserType: r.DocEidUserType),
                (Eid: r.SigEid, UserType: r.SigUserType)
            })
            .Where(p => p.Eid != 0)
            .Distinct()
            .ToList();

        // ── Query 2: Batch-fetch signatory names ──
        var sqlNames = @"
            SELECT eid AS Eid, user_type AS UserType, fname AS Fname,
                   position AS Position, office_name AS OfficeName
            FROM [bacpdfsign].[dbo].[signatory_names]
            WHERE eid IN @Eids";

        var allEids = namePairs.Select(p => p.Eid).Distinct().ToList();
        var nameResults = (await _dbService.QueryAsync<SignatoryNameDto, dynamic>(
            sqlNames,
            new { Eids = allEids },
            CommandType.Text)).ToList();

        var nameMap = nameResults
            .ToDictionary(n => (n.Eid, n.UserType), n => n);

        // ── Query 3: Batch-fetch last action per sig_id ──
        var sqlLastAction = @"
            ;WITH ActionEvents AS (
                SELECT
                    ds_prev.doc_id,
                    ds_prev.sig_order,
                    dsl_prev.sign_datetime AS EventDatetime,
                    'Signature'             AS EventType,
                    ds_prev.sig_eid         AS ActorEid,
                    ds_prev.sig_user_type   AS ActorUserType
                FROM [bacpdfsign].[dbo].[document_signatories] ds_prev
                INNER JOIN [bacpdfsign].[dbo].[document_signature_location] dsl_prev
                    ON ds_prev.sig_id = dsl_prev.sig_id
                WHERE ds_prev.doc_id IN @DocIds
                  AND dsl_prev.sign_datetime IS NOT NULL

                UNION ALL

                SELECT
                    da2.doc_id,
                    0 AS sig_order,
                    da2.doc_datetime  AS EventDatetime,
                    'Document Upload'  AS EventType,
                    da2.doc_eid        AS ActorEid,
                    da2.doc_eid_user_type AS ActorUserType
                FROM [bacpdfsign].[dbo].[document_attach] da2
                WHERE da2.doc_id IN @DocIds
                  AND da2.doc_datetime IS NOT NULL
            ),
            RankedActions AS (
                SELECT
                    ae.doc_id,
                    ae.sig_order AS PrevSigOrder,
                    ae.EventDatetime,
                    ae.EventType,
                    ae.ActorEid,
                    ae.ActorUserType,
                    ROW_NUMBER() OVER (
                        PARTITION BY ae.doc_id
                        ORDER BY ae.sig_order DESC, ae.EventDatetime DESC
                    ) AS rn
                FROM ActionEvents ae
            )
            SELECT
                doc_id       AS DocId,
                EventDatetime AS LastActionDatetime,
                EventType     AS LastActionType,
                ActorEid      AS ActorEid,
                ActorUserType AS ActorUserType
            FROM RankedActions
            WHERE rn = 1";

        var lastActions = (await _dbService.QueryAsync<LastActionDto, dynamic>(
            sqlLastAction,
            new { DocIds = docIds },
            CommandType.Text)).ToList();

        var lastActionMap = lastActions
            .ToDictionary(a => a.DocId, a => a);

        // ── Assemble final results in memory ──
        var results = coreResults.Select(r =>
        {
            var dto = new PendingDocumentDto
            {
                DocId = r.DocId,
                DocName = r.DocName,
                DocCode = r.DocCode,
                DocDescription = r.DocDescription,
                DocTypeId = r.DocTypeId,
                DocCreatedDatetime = r.DocCreatedDatetime,
                DocStatusId = r.DocStatusId,
                DocStatusName = r.DocStatusName,
                DocTypeName = r.DocTypeName,
                DocTypeAbbr = r.DocTypeAbbr,
                SigId = r.SigId,
                SigOrder = r.SigOrder,
                SigEid = r.SigEid,
                SigUserType = r.SigUserType,
                SigStatus = r.SigStatus,
                SigStatusName = r.SigStatusName,
                SignatoryAssignedDatetime = r.SignatoryAssignedDatetime,
                CurrentSignatureDatetime = null,
                SigLevel = r.SigLevel,
                SigSignCount = r.SigSignCount
            };

            // Creator name
            if (nameMap.TryGetValue((r.DocEid, r.DocEidUserType), out var creator))
            {
                dto.DocCreatedByName = creator.Fname;
                dto.DocCreatedByPosition = creator.Position;
                dto.DocCreatedByOffice = creator.OfficeName;
            }

            // Signatory name
            if (nameMap.TryGetValue((r.SigEid, r.SigUserType), out var signatory))
            {
                dto.SignatoryName = signatory.Fname;
                dto.SignatoryPosition = signatory.Position;
                dto.SignatoryOffice = signatory.OfficeName;
            }

            // Last action
            if (lastActionMap.TryGetValue(r.DocId, out var action))
            {
                dto.LastActionDatetime = action.LastActionDatetime;
                dto.LastActionType = action.LastActionType;

                if (nameMap.TryGetValue((action.ActorEid, action.ActorUserType), out var actor))
                {
                    dto.LastActionByName = actor.Fname;
                    dto.LastActionByPosition = actor.Position;
                    dto.LastActionByOffice = actor.OfficeName;
                }
            }

            return dto;
        }).ToList();

        return results;
    }

    // ==================== GET PENDING DOCUMENT TYPES ====================

    public async Task<IEnumerable<PendingDocumentTypeDto>> GetPendingDocumentTypesAsync(int year, int eid, int userType, IEnumerable<int>? allowedDocTypeIds = null)
    {
        // Alternate-signing callers pass their allowed doc type ids here so the
        // tab list only ever shows doc types they're actually authorized to
        // sign for that principal. Normal (non-alternate) callers pass null
        // and get the unfiltered behavior this method always had.
        var filterClause = allowedDocTypeIds != null ? "AND dt.id IN @AllowedDocTypeIds" : "";
        var sql = $@"
            SELECT
                dt.id AS Id,
                dt.document_description AS DocumentDescription,
                COUNT(DISTINCT da.doc_id) AS PendingCount
            FROM
                [bacpdfsign].[dbo].[document_attach] da
                INNER JOIN [bacpdfsign].[dbo].[document_types] dt
                    ON da.doc_type_id = dt.id
                INNER JOIN [bacpdfsign].[dbo].[document_signatories] ds
                    ON da.doc_id = ds.doc_id
            WHERE
                ds.sig_eid = @Eid
                AND ds.sig_user_type = @UserType
                AND ds.sig_status = 0
                AND da.doc_status_id IN (1, 3)
                AND NOT EXISTS (
                    SELECT 1
                    FROM [bacpdfsign].[dbo].[document_signatories] ds2
                    WHERE ds2.doc_id = ds.doc_id
                      AND ds2.sig_order < ds.sig_order
                      AND ds2.sig_status != 1
                )
                AND YEAR(CASE WHEN ISDATE(da.doc_datetime) = 1 THEN CONVERT(datetime, da.doc_datetime, 100) END) = @Year
                {filterClause}
            GROUP BY
                dt.id, dt.document_description
            ORDER BY
                dt.document_description";

        return await _dbService.QueryAsync<PendingDocumentTypeDto, dynamic>(
            sql,
            new { Year = year, Eid = eid, UserType = userType, AllowedDocTypeIds = allowedDocTypeIds ?? Enumerable.Empty<int>() },
            CommandType.Text);
    }

    // ==================== GET DOCUMENT VIEW ====================

    public async Task<DocumentViewResult?> GetDocumentViewAsync(int docId)
    {
        var doc = await _dbService.QueryFirstOrDefaultAsync<DocumentViewDto, dynamic>(
            @"SELECT da.doc_id AS DocId, da.doc_name AS DocName, da.doc_code AS DocCode,
                     da.doc_description AS DocDescription, da.doc_status_id AS DocStatusId,
                     rs.status_type AS DocStatusName, da.doc_type_id AS DocTypeId,
                     dt.document_description AS DocTypeName, dt.document_abbr AS DocTypeAbbr,
                     dt.isbulk AS IsBulk, dt.isShow AS IsShow, da.doc_datetime AS DocDatetime,
                     CASE WHEN ISDATE(da.doc_datetime) = 1 THEN CONVERT(datetime, da.doc_datetime, 100) END AS DocDatetimeParsed,
                     da.doc_pages AS DocPages, da.doc_eid AS DocEid, da.doc_eid_user_type AS DocEidUserType,
                     sn.fname AS UploadedByName, sn.position AS UploadedByPosition, sn.office_name AS UploadedByOffice
              FROM [bacpdfsign].[dbo].[document_attach] da
              LEFT JOIN [bacpdfsign].[dbo].[document_types] dt ON da.doc_type_id = dt.id
              LEFT JOIN [bacpdfsign].[dbo].[req_status] rs ON da.doc_status_id = rs.id
              LEFT JOIN [bacpdfsign].[dbo].[signatory_names] sn ON da.doc_eid = sn.eid AND da.doc_eid_user_type = sn.user_type
              WHERE da.doc_id = @DocId",
            new { DocId = docId }, CommandType.Text);

        if (doc == null)
            return null;

        var result = new DocumentViewResult { Document = doc };

        var signatories = (await _dbService.QueryAsync<DocumentSignatoryViewDto, dynamic>(
            @"SELECT ds.sig_id AS SigId, ds.doc_id AS DocId, ds.sig_code AS SigCode, ds.sig_eid AS SigEid,
                     ds.sig_status AS SigStatus, ds.sig_order AS SigOrder, ds.sig_remarks AS SigRemarks,
                     ds.sig_user_type AS SigUserType, ds.sig_level AS SigLevel, ds.sig_sign_count AS SigSignCount,
                     ds.sig_remarks_datenTime AS SigRemarksDatenTime, ds.date_time_inserted AS DateTimeInserted,
                     rss.id AS SignStatusId, rss.description AS SignStatusDescription,
                     sn.fname AS Fname, sn.position AS Position, sn.office_name AS OfficeName
              FROM [bacpdfsign].[dbo].[document_signatories] ds
              INNER JOIN [bacpdfsign].[dbo].[req_sign_status] rss ON ds.sig_status = rss.id
              LEFT JOIN [bacpdfsign].[dbo].[signatory_names] sn ON ds.sig_eid = sn.eid AND ds.sig_user_type = sn.user_type
              WHERE ds.doc_id = @DocId
              ORDER BY ds.sig_order, ds.date_time_inserted",
            new { DocId = docId }, CommandType.Text)).ToList();

        if (signatories.Count == 0)
            return result;

        // Latest stamp per sig_id (a signatory could in theory carry multiple stamps
        // across pages — MAX collapses that to a single "when they signed" instant).
        var sigIds = signatories.Select(s => s.SigId).Distinct().ToList();
        var signDates = (await _dbService.QueryAsync<SigSignDateDto, dynamic>(
            // sign_datetime is nvarchar, not a native datetime column — MAX() on the raw
            // string sorts alphabetically ("Jul 1" < "Jun 30" as text), so it must be
            // converted before aggregating or a multi-stamp signatory crossing a month
            // boundary can silently pick the wrong "latest" timestamp.
            @"SELECT sig_id AS SigId,
                     MAX(CASE WHEN ISDATE(sign_datetime) = 1 THEN CONVERT(datetime, sign_datetime, 100) END) AS SignDatetime
              FROM [bacpdfsign].[dbo].[document_signature_location]
              WHERE sig_id IN @SigIds
              GROUP BY sig_id",
            new { SigIds = sigIds }, CommandType.Text))
            .ToDictionary(s => s.SigId, s => s.SignDatetime);

        foreach (var s in signatories)
            if (signDates.TryGetValue(s.SigId, out var dt))
                s.SignDatetime = dt;

        var orderedGroups = signatories
            .GroupBy(s => s.SigOrder)
            .OrderBy(g => g.Key)
            .ToList();

        // Seed with the doc's upload date — the first sig_order group "receives" the
        // document then. Each subsequent group's "received" date is the previous
        // group's last signature; carried forward unchanged if that group hasn't
        // finished signing yet (shouldn't happen given the sign-in-order invariant,
        // but keeps the value sane rather than nulling out mid-flow).
        DateTime? previousGroupMaxSign = doc.DocDatetimeParsed;

        foreach (var group in orderedGroups)
        {
            var members = group.ToList();

            foreach (var s in members)
            {
                s.ReceivedDatetime = previousGroupMaxSign;
                if (previousGroupMaxSign.HasValue)
                {
                    // Once signed, this is how long that signatory actually took —
                    // received to their own sign_datetime, a fixed historical fact.
                    // Still-pending signatories have no sign_datetime yet, so fall
                    // back to received-to-now (how long they've been sitting so far).
                    var until = s.SignDatetime ?? DateTime.Now;
                    // Calendar-date difference, not elapsed hours — a document received
                    // at 11pm and signed at 1am the same "day" in user terms would
                    // otherwise show 0 or 1 depending on time-of-day parity. Comparing
                    // .Date to .Date guarantees same-calendar-day always reads as 0d.
                    s.DaysPending = (until.Date - previousGroupMaxSign.Value.Date).Days;
                }
            }

            // Countersign only resolved for a clean 2-person group: one member shows
            // sig_status = 1 with no signature stamp of their own, the other has one.
            if (members.Count == 2)
            {
                var a = members[0];
                var b = members[1];
                if (a.SigStatus == 1 && !signDates.ContainsKey(a.SigId) && signDates.ContainsKey(b.SigId))
                {
                    a.CounterSignedByName = b.Fname;
                    a.CounterSignedByPosition = b.Position;
                }
                if (b.SigStatus == 1 && !signDates.ContainsKey(b.SigId) && signDates.ContainsKey(a.SigId))
                {
                    b.CounterSignedByName = a.Fname;
                    b.CounterSignedByPosition = a.Position;
                }
            }

            var signedMembers = members.Where(s => s.SignDatetime.HasValue).ToList();
            if (signedMembers.Count > 0)
                previousGroupMaxSign = signedMembers.Max(s => s.SignDatetime!.Value);
        }

        var grouped = orderedGroups
            .Select(g => new DocumentSignatoryViewGroupDto
            {
                SigOrder = g.Key,
                SignatoryCount = g.Count(),
                Signatories = g.ToList()
            })
            .ToList();

        result.Signatories = signatories;
        result.Grouped = grouped;
        return result;
    }

    // ==================== GET SIGNATURE IMAGE (merged: signature or initial) ====================

    public async Task<SignatureImageResult> GetSignatureImageMergedAsync(string eids, string usertypes, string type = "signature")
    {
        var credentials = _credentialService.GetNetworkCredential();
        var userTypeFolder = Path.Combine(DigitalSpicimenPath, usertypes);
        var eidFolder = Path.Combine(userTypeFolder, eids);

        string pngPath;

        if (type == "signature")
        {
            var sql = @"
                SELECT a.id, a.pfx_attachement, a.passwords, a.signatures, a.eid, a.code, b.fname
                FROM bacpdfsign.dbo.pfx_attachments AS a
                LEFT JOIN [bacpdfsign].[dbo].[signatory_names] AS b ON a.eid = b.eid
                WHERE a.eid = @Eid AND a.user_type = @UserType AND a.active = 1";
            var result = await _dbService.QueryFirstOrDefaultAsync<dynamic, dynamic>(
                sql, new { Eid = eids, UserType = usertypes }, CommandType.Text);

            if (result == null)
                return new SignatureImageResult { NotFound = true };

            string cert_signature = result.signatures?.ToString() ?? "";
            string code = result.code?.ToString() ?? eids;
            var codeFolder = Path.Combine(eidFolder, code);

            var p12Path = Path.Combine(codeFolder, $"cert_{eids}.p12");
            pngPath = Path.Combine(codeFolder, $"signature_{eids}.png");

            try
            {
                using (_fileStorage.Connect(DigitalSpicimenPath))
                {
                    if (!await _fileStorage.FileExistsAsync(p12Path) && result.pfx_attachement != null)
                    {
                        byte[] rawCert = (byte[])result.pfx_attachement;
                        await _fileStorage.WriteFileAsync(p12Path, rawCert);
                    }
                    if (!await _fileStorage.FileExistsAsync(pngPath) && !string.IsNullOrEmpty(cert_signature))
                    {
                        var cleanSig = Regex.Replace(cert_signature, @"^[\w/\:.-]+;base64,", string.Empty);
                        await _fileStorage.WriteFileAsync(pngPath, Convert.FromBase64String(cleanSig));
                    }

                    var imageBytes = await _fileStorage.ReadFileAsync(pngPath);
                    if (imageBytes != null)
                    {
                        return new SignatureImageResult { Bytes = imageBytes, ContentType = "image/png" };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("NAS unavailable for get_signature_image_merged: {0}", ex.Message);
            }

            // NAS unavailable — serve directly from DB
            if (!string.IsNullOrEmpty(cert_signature))
            {
                var cleanSig = Regex.Replace(cert_signature, @"^[\w/\:.-]+;base64,", string.Empty);
                return new SignatureImageResult { Bytes = Convert.FromBase64String(cleanSig), ContentType = "image/png" };
            }

            return new SignatureImageResult { NotFound = true };
        }
        else if (type == "initial")
        {
            var sql = @"
                SELECT initial_signature, code
                FROM bacpdfsign.dbo.pfx_initial_signature
                WHERE eid = @Eid AND user_type = @UserType AND active = 1";
            var result = await _dbService.QueryFirstOrDefaultAsync<dynamic, dynamic>(
                sql, new { Eid = eids, UserType = usertypes }, CommandType.Text);

            if (result == null)
                return new SignatureImageResult { NotFound = true };

            string initial_signature = result.initial_signature?.ToString() ?? "";
            string code = result.code?.ToString() ?? eids;
            var codeFolder = Path.Combine(eidFolder, code);
            pngPath = Path.Combine(codeFolder, $"initial_{eids}.png");

            try
            {
                using (_fileStorage.Connect(DigitalSpicimenPath))
                {
                    if (!await _fileStorage.FileExistsAsync(pngPath) && !string.IsNullOrEmpty(initial_signature))
                    {
                        var cleanSig = Regex.Replace(initial_signature, @"^[\w/\:.-]+;base64,", string.Empty);
                        await _fileStorage.WriteFileAsync(pngPath, Convert.FromBase64String(cleanSig));
                    }

                    var imageBytes = await _fileStorage.ReadFileAsync(pngPath);
                    if (imageBytes != null)
                    {
                        return new SignatureImageResult { Bytes = imageBytes, ContentType = "image/png" };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("NAS unavailable for get_signature_image_merged (initial): {0}", ex.Message);
            }

            // NAS unavailable — serve directly from DB
            if (!string.IsNullOrEmpty(initial_signature))
            {
                var cleanSig = Regex.Replace(initial_signature, @"^[\w/\:.-]+;base64,", string.Empty);
                return new SignatureImageResult { Bytes = Convert.FromBase64String(cleanSig), ContentType = "image/png" };
            }

            return new SignatureImageResult { NotFound = true };
        }
        else
        {
            return new SignatureImageResult { InvalidType = true };
        }
    }

    // ==================== GET SIGNATORY BY SIG ID ====================

    public async Task<DocumentSignatoryDto?> GetSignatoryBySigIdAsync(long sigId)
    {
        var sql = @"
            SELECT TOP (1)
                  sig_id AS SigId,
                  doc_id AS DocId,
                  sig_code AS SigCode,
                  sig_eid AS SigEid,
                  sig_status AS SigStatus,
                  sig_order AS SigOrder,
                  sig_remarks AS SigRemarks,
                  sig_query_signed AS SigQuerySigned,
                  sig_query_return AS SigQueryReturn,
                  sig_user_type AS SigUserType,
                  sig_level AS SigLevel,
                  sig_sign_count AS SigSignCount,
                  sig_remarks_datenTime AS SigRemarksDatenTime,
                  date_time_inserted AS DateTimeInserted
              FROM bacpdfsign.dbo.document_signatories
              WHERE sig_id = @SigId;";

        return await _dbService.QueryFirstOrDefaultAsync<DocumentSignatoryDto, dynamic>(
            sql, new { SigId = sigId }, CommandType.Text);
    }

    // ==================== GET PFX ATTACHMENTS BY EID (has-password check) ====================

    public async Task<bool> GetPfxAttachmentsByEidAsync(int eid, int userType)
    {
        var sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                  FROM [bacpdfsign].[dbo].[pfx_attachments]
                 WHERE eid = @Eid
                   AND user_type = @UserType
                   AND active = 1
                   AND ISNULL(LTRIM(RTRIM(passwords)), '') <> ''
            ) THEN 1 ELSE 0 END AS HasPassword;";

        var hasPassword = await _dbService.ExecuteScalarAsync<int, dynamic>(
            sql,
            new { Eid = eid, UserType = userType },
            CommandType.Text);

        return hasPassword == 1;
    }

    // ==================== GET PINCODE BY EID (pin match check) ====================

    public async Task<bool> GetPincodeByEidAsync(int eid, int userType, string pincode)
    {
        var sql = @"
                    SELECT CASE WHEN EXISTS (
                        SELECT 1
                          FROM [bacpdfsign].[dbo].[pfx_attachments]
                         WHERE eid = @Eid
                           AND user_type = @UserType
                           AND active = 1
                           AND ISNULL(LTRIM(RTRIM(pin_code)), '') = ISNULL(LTRIM(RTRIM(@PinCode)), '')
                    ) THEN 1 ELSE 0 END AS MatchExists;";

        var match = await _dbService.ExecuteScalarAsync<int, dynamic>(
            sql,
            new { Eid = eid, UserType = userType, PinCode = pincode },
            CommandType.Text);

        return match == 1;
    }

    // ==================== GET CHECKPASSWORD BY EID ====================

    public async Task<int> GetCheckPasswordByEidAsync(int eid, int userType, string password)
    {
        var eidSignature = eid.ToString();
        var eidUtSignature = userType.ToString();

        // 1. Fetch the active PFX record (we need both the blob AND the code for the folder)
        var pfxRecord = await _dbService.QueryFirstOrDefaultAsync<PfxDetails, dynamic>(
            @"SELECT TOP 1
                id              AS PfxId,
                pfx_attachement AS PfxAttachment,
                passwords       AS Password,
                signatures      AS Signature,
                eid             AS Eid,
                code            AS Code,
                NULL            AS Fname
              FROM [bacpdfsign].[dbo].[pfx_attachments]
              WHERE eid       = @Eid
                AND user_type = @UserType
                AND active    = 1",
            new { Eid = eid, UserType = userType },
            CommandType.Text);

        if (pfxRecord == null || pfxRecord.PfxAttachment == null || pfxRecord.PfxAttachment.Length == 0)
            return 0;

        // 2. Build the NAS path using the code column as the innermost folder layer:
        //    DigitalSpicimenPath \ {userType} \ {eid} \ {code} \ cert_{eid}.p12
        var code = pfxRecord.Code ?? eidSignature;
        var nasUserTypeFolder = Path.Combine(DigitalSpicimenPath, eidUtSignature);
        var nasEidFolder = Path.Combine(nasUserTypeFolder, eidSignature);
        var nasCodeFolder = Path.Combine(nasEidFolder, code);
        var nasCertP12 = Path.Combine(nasCodeFolder, $"cert_{eidSignature}.p12");

        // 3. Open ONE NAS connection that stays alive through the Spire load
        using (_fileStorage.Connect(DigitalSpicimenPath))
        {
            // Write .p12 to NAS if not cached yet (seed from DB blob)
            if (!await _fileStorage.FileExistsAsync(nasCertP12))
                await _fileStorage.WriteFileAsync(nasCertP12, pfxRecord.PfxAttachment);

            // 5. Load the .p12 directly from NAS — password correct → 1, wrong → 0
            try
            {
                _ = new Spire.Pdf.Security.PdfCertificate(nasCertP12, password);
                return 1;
            }
            catch
            {
                return 0;
            }
        }
    }

    // ==================== GET PDF DIGITAL ONLY (Converted from legacy ASP.NET) ====================

    public async Task<PdfDigitalOnlyResult> GetPdfDigitalOnlyAsync(int formId)
    {
        // 1. Get document details. `doc_attachement` is skipped here — it's
        // a potentially large blob only needed for doc_type 0 documents that
        // don't have a signed/NAS copy yet (status 1, or 4/5/7/8/9 fallback).
        // The common case (status 2/3, already signed) never touches it, so
        // fetching + double-casting it on every call was pure wasted I/O —
        // it's loaded separately below only when actually required.
        var docDetails = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachDetails, dynamic>(
            @"SELECT doc_id AS DocId, doc_is AS DocIs, doc_description AS DocDescription,
              doc_type_id AS DocTypeId, doc_type AS DocType, doc_directory AS DocDirectory,
              doc_name AS DocName, doc_code AS DocCode, doc_status_id AS DocStatusId,
              doc_datetime AS DocDatetime
              FROM [bacpdfsign].[dbo].[document_attach] WHERE doc_id = @FormId",
            new { FormId = formId },
            CommandType.Text);

        if (docDetails == null)
            return new PdfDigitalOnlyResult { NotFoundMessage = "Document not found" };

        var credentials = _credentialService.GetNetworkCredential();
        var networkPath = _fileStorage.RootPath;
        var description = Regex.Replace(docDetails.DocDescription ?? "", "[^a-zA-Z0-9]", "");
        byte[] pdfBytes;

        // ─── STATUS 2 or 3: Signed / Partially Signed ───
        if (docDetails.DocStatusId == 2 || docDetails.DocStatusId == 3)
        {
            // Legacy documents (doc_type 0/1) were signed and saved straight
            // to the NAS share before the app migrated to the configured
            // FileStorage backend (S3/Local/Custom) — they were never copied
            // over, so reading them through _fileStorage (which now points
            // at that backend, not the NAS) would never find them. Read
            // straight off the NAS share instead, using the classic
            // digital_signature\{yy}\Form{docId}\file.pdf convention.
            if (docDetails.DocType == 0 || docDetails.DocType == 1)
            {
                var signedBytes = await ReadSignedPdfFromNasAsync(formId.ToString(), credentials);
                if (signedBytes == null)
                    return new PdfDigitalOnlyResult { NotFoundMessage = "Signed PDF not found on NAS." };

                pdfBytes = signedBytes;
            }
            else
            {
                using (_fileStorage.Connect(DigitalSignaturePath))
                {
                    var mainPdf = await FindSignedPdfPathAsync(formId.ToString());
                    var signedBytes = await ReadFileWithRetryAsync(mainPdf);
                    if (signedBytes == null)
                        return new PdfDigitalOnlyResult { NotFoundMessage = "Signed PDF not found on NAS." };

                    pdfBytes = signedBytes;
                }
            }
        }
        // ─── STATUS 1: Pending (no signatures yet) ───
        else if (docDetails.DocStatusId == 1)
        {
            if (docDetails.DocType == 0)
                await MigrateLegacyBinaryIfNeededAsync(docDetails);

            var originalBytes = await GetOriginalPdfBytes(docDetails, credentials, networkPath);

            if (originalBytes == null || originalBytes.Length == 0)
                return new PdfDigitalOnlyResult { NotFoundMessage = "Original PDF not found." };

            pdfBytes = originalBytes;
        }
        // ─── STATUS 4,5,7,8,9: Returned / Clerical Error / Lacking Attachment ───
        else if (docDetails.DocStatusId is 4 or 5 or 7 or 8 or 9)
        {
            // Determine watermark text based on status
            var watermarkText = docDetails.DocStatusId switch
            {
                4 or 5 or 7 => "Returned",
                8 => "Clerical Error",
                9 => "Lacking Attachment",
                _ => "Returned"
            };

            // Try to get the signed PDF from the form folder first
            byte[]? sourcePdfBytes = null;

            using (_fileStorage.Connect(DigitalSignaturePath))
            {
                var mainPdf = await FindSignedPdfPathAsync(formId.ToString());
                sourcePdfBytes = await ReadFileWithRetryAsync(mainPdf);
            }

            // If not found, fall back to the original document
            if (sourcePdfBytes == null || sourcePdfBytes.Length == 0)
            {
                sourcePdfBytes = await GetOriginalPdfBytes(docDetails, credentials, networkPath);
            }

            if (sourcePdfBytes == null || sourcePdfBytes.Length == 0)
                return new PdfDigitalOnlyResult { NotFoundMessage = "PDF not found for this document." };

            // Apply watermark
            pdfBytes = ApplyWatermark(sourcePdfBytes, watermarkText);
        }
        else
        {
            return new PdfDigitalOnlyResult { BadRequestMessage = $"Unsupported document status: {docDetails.DocStatusId}" };
        }

        // ─── Return PDF response ───
        if (pdfBytes == null || pdfBytes.Length == 0)
            return new PdfDigitalOnlyResult { NotFoundMessage = "PDF file is empty." };

        return new PdfDigitalOnlyResult { PdfBytes = pdfBytes, FileName = $"{description}.pdf" };
    }

    /// <summary>
    /// Lazily migrates a legacy binary-blob document (doc_type == 0) to file
    /// storage the first time its bytes are requested while still unsigned
    /// (called from GetPdfDigitalOnlyAsync's STATUS 1 branch). Writes the
    /// pristine PDF to a permanent original/file.pdf copy plus the normal
    /// working file.pdf, verifies both round-trip correctly, then flips
    /// doc_type/doc_directory/doc_name and clears doc_attachement.
    /// Best-effort: any failure leaves the row untouched and the caller
    /// keeps serving from the existing blob, so a hiccup here never breaks
    /// the view/sign request that triggered it.
    /// </summary>
    private async Task MigrateLegacyBinaryIfNeededAsync(DocumentAttachDetails docDetails)
    {
        try
        {
            var sourceBytes = await LoadLegacyBlobBytesAsync(docDetails);
            if (sourceBytes == null || sourceBytes.Length == 0)
            {
                _logger.LogWarning("Legacy binary migration skipped — no blob bytes for doc_id: {DocId}", docDetails.DocId);
                return;
            }

            if (!IsPdfHeader(sourceBytes))
            {
                _logger.LogWarning("Legacy binary migration skipped — blob is not a valid PDF for doc_id: {DocId}", docDetails.DocId);
                return;
            }

            var yearFolder = (DateTime.Now.Year % 100).ToString();
            var digSigLeaf = Path.GetFileName(DigitalSignaturePath.TrimEnd(Path.DirectorySeparatorChar));
            var targetDirFull = Path.Combine(DigitalSignaturePath, yearFolder, $"Form{docDetails.DocId}");
            var docDirectoryRelative = Path.Combine(digSigLeaf, yearFolder, $"Form{docDetails.DocId}");

            var originalDirFull = Path.Combine(targetDirFull, "original");
            var originalPath = Path.Combine(originalDirFull, "file.pdf");
            var workingPath = Path.Combine(targetDirFull, "file.pdf");

            // Preserve the pristine original once, permanently — never
            // overwritten again after this point.
            if (!await _fileStorage.FileExistsAsync(originalPath))
            {
                var (originalSaved, _) = await WriteFileWithRetryAsync(originalPath, sourceBytes, originalDirFull);
                if (!originalSaved)
                {
                    _logger.LogWarning("Legacy binary migration failed to write original copy for doc_id: {DocId}", docDetails.DocId);
                    return;
                }
            }

            // Working copy — same path/name convention the rest of the app
            // (upload, signing, GetOriginalPdfBytes) already expects.
            var (workingSaved, _) = await WriteFileWithRetryAsync(workingPath, sourceBytes, targetDirFull);
            if (!workingSaved)
            {
                _logger.LogWarning("Legacy binary migration failed to write working copy for doc_id: {DocId}", docDetails.DocId);
                return;
            }

            // Verify both writes round-trip correctly before touching the DB.
            var originalReadBack = await ReadFileWithRetryAsync(originalPath);
            var workingReadBack = await ReadFileWithRetryAsync(workingPath);

            if (!BytesMatchPdf(originalReadBack, sourceBytes) || !BytesMatchPdf(workingReadBack, sourceBytes))
            {
                _logger.LogWarning("Legacy binary migration verification failed for doc_id: {DocId}", docDetails.DocId);
                await LogError("system", "migration", docDetails.DocId.ToString(), "Legacy binary migration verification failed — PDF write did not round-trip correctly.");
                return;
            }

            var newDocType = MapStorageModeToDocType(_fileStorage.Mode);
            var rowsAffected = await _dbService.ExecuteAsync<dynamic>(
                @"UPDATE bacpdfsign.dbo.document_attach
                  SET doc_directory = @DocDirectory, doc_name = @DocName,
                      doc_type = @DocType, doc_attachement = NULL
                  WHERE doc_id = @DocId AND doc_type = 0",
                new
                {
                    DocDirectory = docDirectoryRelative,
                    DocName = "file.pdf",
                    DocType = newDocType,
                    docDetails.DocId
                },
                CommandType.Text);

            if (rowsAffected > 0)
            {
                // Reflect the migration in the caller's in-memory copy so the
                // very next GetOriginalPdfBytes call in this same request
                // reads the freshly-written file instead of the (now-gone) blob.
                docDetails.DocType = newDocType;
                docDetails.DocDirectory = docDirectoryRelative;
                docDetails.DocName = "file.pdf";
                docDetails.DocAttachment = null;
                docDetails.DocAttachmentText = null;
                _logger.LogInformation("Migrated legacy binary document to file storage for doc_id: {DocId}", docDetails.DocId);
            }
            // rowsAffected == 0 means another concurrent request already
            // migrated this doc first — harmless, nothing else to do.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Legacy binary migration threw for doc_id: {DocId}", docDetails.DocId);
        }
    }

    private static bool IsPdfHeader(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;

    private static bool BytesMatchPdf(byte[]? readBack, byte[] source) =>
        readBack != null && readBack.Length == source.Length && IsPdfHeader(readBack);

    // ==================== SAVE SIGNATURE IMAGE (Converted from legacy ASP.NET) ====================

    public async Task<SaveSignatureResult> SaveSignatureImageAsync(SaveSignatureRequest request)
    {
        // The certificate, PIN/password, signature image, and stamped name always
        // belong to the person who actually logged in and submitted this request —
        // never the principal being signed for. This holds even in the
        // alternate/delegate flow: an alternate authenticates and signs with
        // their OWN credentials; only the workflow bookkeeping below (which
        // pending row gets closed, and the new row recorded for the alternate)
        // references the principal's identity (request.VwEids/VwUserType).
        var eidSignature = request.Eid;
        var eidUtSignature = request.UserType;

        // Declared here (not inside the try below) so the outer catch can also
        // roll it back — an exception from Spire/PDF work between opening this
        // transaction and committing it must never leave it dangling open.
        IDbTransaction? alternateTx = null;

        try
        {
            // 1. Get sig_id for the current signatory (non-alternate/delegate path)
            var signId = await GetSigId(request.DocId, request.Eid, request.UserType);
            var signIdAlternate = "";
            var signIdDelegate = "";
            var signFname = await GetSigFname(request.Eid, request.UserType);
            var isAltSpecimenAvailable = await GetAlterSpecimen(request.Eid, request.UserType);

            // The principal's pending document_signatories row that this
            // alternate/delegate action satisfies — populated only when
            // IsAlternate is set. The alternate signs a brand-new row cloned
            // from this one (inserted just before the signature-location loop
            // below) rather than reusing it directly, so the signatories table
            // always records who actually signed alongside the principal's
            // now-closed row.
            PrincipalSignatoryRowDto? principalRow = null;

            if (request.IsAlternate != 0)
            {
                if (request.IsDelegate != 0)
                {
                    signIdDelegate = await GetSigIdDesignated(request.DocId, request.Eid, request.UserType, request.VwEids, request.VwUserType);
                    principalRow = await GetSignatoryRowForClosureAsync(signIdDelegate);
                }
                else
                {
                    signIdAlternate = await GetSigIdAlternate(request.DocId, request.Eid, request.UserType, request.VwEids, request.VwUserType);
                    principalRow = await GetSignatoryRowForClosureAsync(signIdAlternate);
                }

                if (principalRow == null)
                {
                    await LogError(eidSignature, eidUtSignature, request.DocId,
                        "No pending signatory row found for the principal being signed for");
                    return new SaveSignatureResult { Success = false, Message = "This document is no longer pending your principal's signature." };
                }
            }

            // 2. Get document details
            var docDetails = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachDetails, dynamic>(
                @"SELECT doc_id AS DocId, doc_is AS DocIs, doc_description AS DocDescription,
                  doc_type_id AS DocTypeId, doc_type AS DocType, doc_directory AS DocDirectory,
                  doc_name AS DocName, doc_code AS DocCode, doc_status_id AS DocStatusId,
                  doc_attachement AS DocAttachment
                  FROM [bacpdfsign].[dbo].[document_attach] WHERE doc_id = @DocId",
                new { DocId = request.DocId },
                CommandType.Text);

            if (docDetails == null)
                return new SaveSignatureResult { Success = false, Message = "Document not found" };

            // Re-validate, at the moment of signing, that the (VwEids/VwUserType,
            // Eid/UserType) PAIR is currently an active alternate relationship
            // for this document's type — the frontend's queue listing already
            // filters to this, but the mutation itself must never trust a
            // client-supplied isAlternate/vwEids/vwUserType without its own
            // server-side check. NOTE: this validates the relationship, not
            // that the caller truly IS request.Eid — DGSignController does not
            // bind Eid/UserType from the JWT before calling this service (unlike
            // PublicSignController, which does), so a caller could in principle
            // submit someone else's Eid. That gap is pre-existing and not
            // specific to the alternate flow (the PIN/password check below is
            // what actually gates signing as a given Eid); it isn't closed here.
            if (principalRow != null)
            {
                var principalEidInt = int.TryParse(request.VwEids, out var pEid) ? pEid : (int?)null;
                var principalUserTypeInt = int.TryParse(request.VwUserType, out var pUt) ? pUt : (int?)null;
                var alternateEidInt = int.TryParse(request.Eid, out var aEid) ? aEid : (int?)null;
                var alternateUserTypeInt = int.TryParse(request.UserType, out var aUt) ? aUt : (int?)null;

                if (principalEidInt is null || principalUserTypeInt is null || alternateEidInt is null || alternateUserTypeInt is null
                    || !await IsActiveAlternateForSigningAsync(principalEidInt.Value, principalUserTypeInt.Value, alternateEidInt.Value, alternateUserTypeInt.Value)
                    || !await IsDocTypeAllowedForAlternateAsync(principalEidInt.Value, principalUserTypeInt.Value, alternateEidInt.Value, alternateUserTypeInt.Value, docDetails.DocTypeId))
                {
                    await LogError(eidSignature, eidUtSignature, request.DocId,
                        "Caller is not currently an authorized alternate for this principal/document type");
                    return new SaveSignatureResult { Success = false, Message = "You are not currently authorized to sign this document as an alternate." };
                }
            }

            // 3. Resolve PDF path and read bytes while network connection is open
            var credentials = _credentialService.GetNetworkCredential();
            byte[]? pdfBytes = null;
            string pdfPath = string.Empty;

            if (docDetails.DocStatusId == 2 || docDetails.DocStatusId == 3 ||
                docDetails.DocStatusId == 10 || docDetails.DocStatusId == 11)
            {
                using (_fileStorage.Connect(DigitalSignaturePath))
                {
                    pdfPath = await FindSignedPdfPathAsync(request.DocId);
                    pdfBytes = await _fileStorage.ReadFileAsync(pdfPath);
                }
            }
            else if (docDetails.DocType == 0)
            {
                pdfPath = "(blob)";
                pdfBytes = docDetails.DocAttachment;
            }
            else
            {
                if (string.IsNullOrEmpty(docDetails.DocDirectory) || string.IsNullOrEmpty(docDetails.DocName))
                {
                    await LogError(eidSignature, eidUtSignature, request.DocId, "Document directory or name not configured");
                    return new SaveSignatureResult { Success = false, Message = "Document path not configured" };
                }

                pdfPath = Path.Combine(NetworkPath, docDetails.DocDirectory, docDetails.DocName);
                using (_fileStorage.Connect(NetworkPath))
                {
                    pdfBytes = await _fileStorage.ReadFileAsync(pdfPath);

                    // First signature on this file-based document — pdfPath is
                    // about to be overwritten with the signed copy (same path
                    // SaveSignatureImageAsync writes to below), so snapshot the
                    // untouched original as a permanent backup before that
                    // happens. Written once: every later signature on this doc
                    // takes the doc_status_id 2/3/10/11 branch above instead
                    // (reads/writes only file.pdf), so this never runs again.
                    if (pdfBytes != null)
                    {
                        var originalBackupPath = Path.Combine(Path.GetDirectoryName(pdfPath) ?? NetworkPath, "original.pdf");
                        if (!await _fileStorage.FileExistsAsync(originalBackupPath))
                        {
                            await _fileStorage.WriteFileAsync(originalBackupPath, pdfBytes);
                        }
                    }
                }
            }

            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                await LogError(eidSignature, eidUtSignature, request.DocId, $"PDF not found at {pdfPath}");
                return new SaveSignatureResult { Success = false, Message = "PDF file not found" };
            }

            // 4. Get PFX certificate details
            PfxDetails pfxDetails;
            try
            {
                pfxDetails = await _dbService.QueryFirstOrDefaultAsync<PfxDetails, dynamic>(
                    @"SELECT a.id AS PfxId, a.pfx_attachement AS PfxAttachment, a.passwords AS Password,
                      a.signatures AS Signature, a.eid AS Eid, a.code AS Code, b.fname AS Fname
                      FROM bacpdfsign.dbo.pfx_attachments AS a
                      LEFT JOIN [bacpdfsign].[dbo].[signatory_names] AS b ON a.eid = b.eid
                      WHERE a.eid = @Eid AND a.active = 1 AND a.user_type = @UserType",
                    new { Eid = eidSignature, UserType = int.Parse(eidUtSignature) },
                    CommandType.Text);

                if (pfxDetails == null)
                {
                    await LogError(eidSignature, eidUtSignature, request.DocId, "No active PFX certificate found");
                    return new SaveSignatureResult { Success = false, Message = "No active certificate found" };
                }
            }
            catch (Exception e)
            {
                await LogError(eidSignature, eidUtSignature, request.DocId, e.Message + "---fail to get PFX details");
                return new SaveSignatureResult { Success = false, Message = "Something went wrong" };
            }

            // 5. Resolve signature image & PFX cert from the NAS cache
            //    Path: DigitalSpicimenPath \ {userType} \ {eid} \ {code} \ cert_{eid}.p12
            //                                                            \ signature_{eid}.png
            var code = pfxDetails.Code ?? eidSignature;
            var nasUserTypeFolder = Path.Combine(DigitalSpicimenPath, eidUtSignature);
            var nasEidFolder = Path.Combine(nasUserTypeFolder, eidSignature);
            var nasCodeFolder = Path.Combine(nasEidFolder, code);

            // Local temp dir — Spire requires file paths, not byte arrays
            var certDir = Path.Combine(_env.ContentRootPath, "Data", "upload",
                $"temp_folder{eidSignature}", "info", $"signature_folder{code}");
            if (!Directory.Exists(certDir))
                Directory.CreateDirectory(certDir);

            byte[]? sigImageBytes = null;
            byte[]? certBytes = null;

            using (_fileStorage.Connect(DigitalSpicimenPath))
            {
                var nasSignaturePng = Path.Combine(nasCodeFolder, $"signature_{eidSignature}.png");
                var nasCertP12 = Path.Combine(nasCodeFolder, $"cert_{eidSignature}.p12");

                // Cache signature PNG to NAS if not present yet
                if (!await _fileStorage.FileExistsAsync(nasSignaturePng) && !string.IsNullOrEmpty(pfxDetails.Signature))
                {
                    var cleanSig = Regex.Replace(pfxDetails.Signature, @"^[\w/\:.-]+;base64,", string.Empty);
                    await _fileStorage.WriteFileAsync(nasSignaturePng, Convert.FromBase64String(cleanSig));
                }

                // Cache PFX cert to NAS if not present yet
                if (!await _fileStorage.FileExistsAsync(nasCertP12) && pfxDetails.PfxAttachment != null && pfxDetails.PfxAttachment.Length > 0)
                    await _fileStorage.WriteFileAsync(nasCertP12, pfxDetails.PfxAttachment);

                // Read both back into memory while the NAS connection is open
                sigImageBytes = await _fileStorage.ReadFileAsync(nasSignaturePng);
                certBytes = await _fileStorage.ReadFileAsync(nasCertP12);
            }

            // Fall back to DB data if NAS was unavailable
            if (sigImageBytes == null && !string.IsNullOrEmpty(pfxDetails.Signature))
            {
                var cleanSig = Regex.Replace(pfxDetails.Signature, @"^[\w/\:.-]+;base64,", string.Empty);
                sigImageBytes = Convert.FromBase64String(cleanSig);
            }
            if (certBytes == null && pfxDetails.PfxAttachment != null)
                certBytes = pfxDetails.PfxAttachment;

            if (certBytes == null || certBytes.Length == 0)
            {
                await LogError(eidSignature, eidUtSignature, request.DocId, "PFX certificate bytes not found");
                return new SaveSignatureResult { Success = false, Message = "Certificate file not found" };
            }

            // Write to local temp for Spire (Spire.PdfCertificate requires file paths)
            var certPath = Path.Combine(certDir, $"cert_{eidSignature}.p12");
            var sigImagePath = Path.Combine(certDir, $"signature_{eidSignature}.png");
            var noImagePath = Path.Combine(certDir, "no_signature.png");

            await System.IO.File.WriteAllBytesAsync(certPath, certBytes);

            if (sigImageBytes != null)
                await System.IO.File.WriteAllBytesAsync(sigImagePath, sigImageBytes);

            // 6. Load PDF with Spire
            Spire.Pdf.License.LicenseProvider.SetLicenseKey("WHYRM/zCtFDRxmwBALCmKY1bBbX9dEFPZjk21hNB10uadQV5COVC24hiA3vsQkj0zSgEzHh4++mY8SpoOo65Pp1jQyrA5Wpq+QByPk0vlSKdOa21CrLuqaJb/MvO9MEyfhtX4qy2qDY9uVOEr+cqGx74ZIy1ohXBoW77YNnbhNgniw5BhBl6JbsAIC8FuimIv23VJm16b790utw1h7RKSN5fA0iyxkYChcaXj1Gw+qMxM7vEIxB/fCoQoE/cgTd+ChHyXpRt9z8rhbknZ3Gt6Iequ9Q4ggXGCdQB/WXKeS6YFPKFDdCA4W0CQWPm4NZG//mqIY41+krPMuZ5KtVSg0gLVLu2W78iKi6Q1pl4QQjC2r8FvtzI00ienDKokJJX4LL6TY0IDNsYl3eNUu4lJQ4p24mE2ea8ps2OTALvs2GxvY2RsMAaAfR0oSMNTqvURhSTw6dyUgv3tCbjtq3Wl4rRRvPXgyS/1B9GxJrQd8HFvGDTOqvRe4abQxmbdN0RsMqemxFA8kPvwZJv7lqoVkKVrKYmEFDC+byPgLhyNOArVQzDpB2cfgAVIib8Dxjl72mQzJ4ef33FdEenOL0INOUAPhBuLF2/uTUQt5bUOWOT1ZX4ynk53zxS6USp6d6KBHDuoYWxfx3g4flDwARicWkvL6x69l90Jpd5ARVUQAug5SfkNQIPxTlobPSbh/LUW7xaQKfhtUVQyhc1mb/ZNgqSnbgbnwbp1JL+DSth1tr5pxpnK5ZTzyY21Rmndf9oU77KLOgbe8MVcNC110LnaqvaMsKaGOHdF1m0cmdCi9ejcgpWyJ1j4y3c0CIstLiaBA1tgthlE25YVrcdkLROEPOlrXMsEeUO5+DYtQl9E9Ete92cYPXaLC/70Fr9XiMKzsZUf/4dHuIcEtP8414ZPWB5Ibjf4BGfsN4hiMTrhYRDD5qS/YaIRkRXLPogzLLV/m1OR63VL5WyuyFQEdxuSM9WtxSw3/cjzRMImXmZ5MKChNHA2HO5HWhe39gXocbCUsIPTvEb84im2ekjwku+3ldHolDkO7cAYPzlDHVapsMXc870e7+1yI/oEPK81bTWDxK5Tzr1GLd1M2CzaC3bySeJ0N6PdsrPY/DRDWaB7hwQNt7nQy/VZzs+cD9iz1pGbiqDANXApX2hqyUFIdxsTWBpB0F1zY3GcdoEfB+07uR+qQpjclNAUNH+BgUU8S/PkQ/QEImY5RWbj1QqdvV3oI9Z2fppFsF5aGoFjgYllzmirgsi6Vl/H+bOO4bT2Sgs+NUdU5aECuX8zrQ3hFW3A0DRPzJnTPTup9R+EOl2ppzBLf5zUWaFKsX/lOvislq+CspeuBU3vHoe7YkG3eBWCEStzuukUUJnAto/YGM6wCIspAhARfL9mFH02CWZsAnO+k+ywkH2/KY+UQvhjPR2uiubdtVD7zPyJEY8r8uN0Yqq1ocK5yR2JZEuryT3lYqnmeIM7Xt4RPJOMSXWOxjwnflN02B11gsfou/eqoAnCRpwtnuD7kYZMb5rp7EVow4Ez7FDUd2Tb+fImNqC2P2Sl2eXhuzqeyeW/nUGNO/usiS5iCHI0A2sc6cJLT8/nZ74IopPcxYXbtlgAAU5BovivzDQzDpbW6SBAhV+ulqnCiSWIxD2NdnAuQ==");
            Spire.Pdf.License.LicenseProvider.LoadLicense();
            var pdfdoc = new Spire.Pdf.PdfDocument();
            try
            {
                pdfdoc.LoadFromBytes(pdfBytes);
            }
            catch (Exception ex)
            {
                await LogError(eidSignature, eidUtSignature, request.DocId, ex.Message + "---fail to load pdfdoc");
                return new SaveSignatureResult { Success = false, Message = "Something went wrong" };
            }

            // 7. Parse signature locations — prefer structured array; fall back to legacy string
            var signatureLocations = new List<SignatureLocationItem>();

            if (request.Signatures != null && request.Signatures.Count > 0)
            {
                signatureLocations = request.Signatures;
            }
            else if (!string.IsNullOrEmpty(request.Location))
            {
                var sets = request.Location.Split(new[] { "__" }, StringSplitOptions.None);
                foreach (var set in sets)
                {
                    var parts = set.Split(',');
                    if (parts.Length < 7) continue;
                    var viewW = float.Parse(parts[3]);
                    var viewH = float.Parse(parts[4]);
                    signatureLocations.Add(new SignatureLocationItem
                    {
                        Page = int.Parse(parts[0]),
                        XPct = viewW == 0 ? 0 : float.Parse(parts[2]) / viewW,
                        YPct = viewH == 0 ? 0 : float.Parse(parts[1]) / viewH,
                        SpecimenType = int.Parse(parts[5]),
                        AuthorityLevel = int.Parse(parts[6])
                    });
                }
            }

            if (signatureLocations.Count == 0)
                return new SaveSignatureResult { Success = false, Message = "No signature locations provided" };

            // Pre-load alternate/initial specimen image
            string? altImagePath = null;
            if (signatureLocations.Any(s => s.SpecimenType != 0))
            {
                var nasAltPath = await GetAlternateSignatureImage(eidSignature, eidUtSignature, isAltSpecimenAvailable);
                if (!string.IsNullOrEmpty(nasAltPath))
                {
                    var localAltPath = Path.Combine(certDir, $"initial_{eidSignature}.png");
                    var networkPath = _fileStorage.RootPath;
                    try
                    {
                        using (_fileStorage.Connect(networkPath))
                        {
                            var altBytes = await _fileStorage.ReadFileAsync(nasAltPath);
                            if (altBytes != null)
                            {
                                await System.IO.File.WriteAllBytesAsync(localAltPath, altBytes);
                                altImagePath = localAltPath;
                            }
                        }
                    }
                    catch
                    {
                        if (System.IO.File.Exists(nasAltPath))
                            altImagePath = nasAltPath;
                    }
                }
            }

            // Default "No Signature" placeholder
            if (!System.IO.File.Exists(noImagePath))
            {
                var defaultImageBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAUA" +
                                          "AAABCAIAAADY8aERAAAAAXRFWHRk" +
                                          "YXRlOmNyZWF0ZWQyMDE4LTAyLTA5" +
                                          "VDIwOjMzOjEzKzAwMDs1MwAAAABJ" +
                                          "RU5ErkJggg==";
                await System.IO.File.WriteAllBytesAsync(noImagePath, Convert.FromBase64String(defaultImageBase64));
            }

            // ── Resolve & validate PFX password ─────────────────────────────────────
            // Mode A — PinCode supplied:
            //   1. Verify PIN against pfx_attachments.pin_code
            //   2. Pull encrypted password from DB → Rijndael.Decrypt → use as pfxPassword
            //
            // Mode B — BulkPasswords supplied:
            //   1. Verify password via Spire (same check as GetCheckpassword)
            //   2. Use as pfxPassword
            //
            // Both modes fail-fast with a logged BadRequest on any mismatch.
            // ────────────────────────────────────────────────────────────────────────
            string pfxPassword;

            if (!string.IsNullOrWhiteSpace(request.PinCode))
            {
                // ── Mode A: PIN-based authentication ────────────────────────────────
                // Step 1: Verify the PIN matches what is stored in pfx_attachments
                var pinMatch = await _dbService.ExecuteScalarAsync<int, dynamic>(
                    @"SELECT CASE WHEN EXISTS (
                          SELECT 1
                          FROM [bacpdfsign].[dbo].[pfx_attachments]
                          WHERE eid       = @Eid
                            AND user_type = @UserType
                            AND active    = 1
                            AND ISNULL(LTRIM(RTRIM(pin_code)), '') = ISNULL(LTRIM(RTRIM(@PinCode)), '')
                      ) THEN 1 ELSE 0 END",
                    new { Eid = eidSignature, UserType = int.Parse(eidUtSignature), PinCode = request.PinCode },
                    CommandType.Text);

                if (pinMatch != 1)
                {
                    await LogError(eidSignature, eidUtSignature, request.DocId, "Invalid PIN code provided");
                    return new SaveSignatureResult { Success = false, Message = "Invalid PIN code" };
                }

                // Step 2: PIN is correct — decrypt the stored password and use it
                if (string.IsNullOrWhiteSpace(pfxDetails.Password))
                {
                    await LogError(eidSignature, eidUtSignature, request.DocId,
                        "PIN verified but no stored password found in pfx_attachments");
                    return new SaveSignatureResult { Success = false, Message = "Certificate password not configured" };
                }

                try
                {
                    pfxPassword = string.IsNullOrEmpty(pfxDetails.Password)
                        ? string.Empty
                        : SPMS.Rijndael.Decrypt(pfxDetails.Password) ?? string.Empty;
                }
                catch (Exception decryptEx)
                {
                    _logger.LogWarning(decryptEx, "Failed to decrypt stored PFX password for eid: {Eid}", eidSignature);
                    await LogError(eidSignature, eidUtSignature, request.DocId,
                        decryptEx.Message + "---fail to decrypt stored PFX password (PIN mode)");
                    return new SaveSignatureResult { Success = false, Message = "Certificate password could not be decrypted" };
                }

                if (string.IsNullOrEmpty(pfxPassword))
                {
                    await LogError(eidSignature, eidUtSignature, request.DocId, "Decrypted password is empty (PIN mode)");
                    return new SaveSignatureResult { Success = false, Message = "Certificate password is required" };
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.BulkPasswords))
            {
                // ── Mode B: Direct password authentication ───────────────────────────
                // Verify the supplied password against the local .p12 copy
                // (same Spire.PdfCertificate check as GetCheckpassword — throws on wrong password)
                try
                {
                    _ = new Spire.Pdf.Security.PdfCertificate(certPath, request.BulkPasswords);
                }
                catch
                {
                    await LogError(eidSignature, eidUtSignature, request.DocId,
                        "Invalid certificate password provided (BulkPasswords mode)");
                    return new SaveSignatureResult { Success = false, Message = "Invalid certificate password" };
                }

                pfxPassword = request.BulkPasswords;
            }
            else
            {
                // Neither PinCode nor BulkPasswords was supplied
                await LogError(eidSignature, eidUtSignature, request.DocId,
                    "No authentication provided — PinCode and BulkPasswords are both empty");
                return new SaveSignatureResult { Success = false, Message = "Certificate password or PIN code is required" };
            }

            // Verify the resolved password against the local .p12 copy
            // (same Spire.PdfCertificate check as GetCheckpassword — throws on wrong password)
            try
            {
                _ = new Spire.Pdf.Security.PdfCertificate(certPath, pfxPassword);
            }
            catch
            {
                await LogError(eidSignature, eidUtSignature, request.DocId, "Invalid certificate password provided");
                return new SaveSignatureResult { Success = false, Message = "Invalid certificate password" };
            }

            // 8a. For an alternate/delegate signature, open a transaction and
            // insert a brand-new document_signatories row cloned from the
            // principal's pending row (same doc/order/etc.) but under the
            // alternate's own identity, already marked signed, and always at
            // sig_level 4 ("for alternative signature" in
            // signatory_leveltype), regardless of the principal's own level.
            // This gives the signing pass a real row of its own to attach
            // document_signature_location entries to, and lets the insert
            // roll back along with everything else below if anything fails.
            var insertSigIdForLocation = signId;
            var closingSigId = signId;

            if (principalRow != null)
            {
                alternateTx = await _dbService.BeginTransactionAsync();
                try
                {
                    var newAlternateSigId = await _dbService.ExecuteScalarAsync<int, dynamic>(
                        @"INSERT INTO bacpdfsign.dbo.document_signatories
                            (doc_id, sig_code, sig_eid, sig_status, sig_order, sig_remarks,
                             sig_query_signed, sig_query_return, sig_user_type, sig_level,
                             sig_sign_count, sig_remarks_datenTime, date_time_inserted)
                          VALUES
                            (@DocId, @SigCode, @SigEid, 1, @SigOrder, '',
                             @QuerySigned, @QueryReturn, @SigUserType, @SigLevel,
                             @SigSignCount, '', CONVERT(NVARCHAR(50), GETDATE(), 100));
                          SELECT SCOPE_IDENTITY();",
                        new
                        {
                            principalRow.DocId,
                            principalRow.SigCode,
                            SigEid = request.Eid,
                            principalRow.SigOrder,
                            QuerySigned = (object?)principalRow.SigQuerySigned ?? DBNull.Value,
                            QueryReturn = (object?)principalRow.SigQueryReturn ?? DBNull.Value,
                            SigUserType = request.UserType,
                            SigLevel = 4,
                            principalRow.SigSignCount
                        },
                        CommandType.Text,
                        alternateTx);

                    // SCOPE_IDENTITY() returning NULL/0 (e.g. an INSTEAD OF
                    // trigger on this table) must not silently proceed — that
                    // would attach every signature-location row to sig_id 0
                    // and still commit a "successful" sign with no recoverable
                    // location record.
                    if (newAlternateSigId <= 0)
                        throw new InvalidOperationException("New alternate signatory row did not return a valid sig_id");

                    insertSigIdForLocation = newAlternateSigId.ToString();
                    closingSigId = request.IsDelegate != 0 ? signIdDelegate : signIdAlternate;
                }
                catch (Exception ec)
                {
                    await _dbService.RollbackTransactionAsync(alternateTx);
                    await LogError(eidSignature, eidUtSignature, request.DocId,
                        ec.Message + "---fail to insert alternate signatory row");
                    return new SaveSignatureResult { Success = false, Message = "Something went wrong" };
                }
            }

            // 8. Apply each signature to the PDF and insert DB records
            foreach (var sigLoc in signatureLocations)
            {
                var pageIndex = sigLoc.Page - 1;
                if (pageIndex < 0 || pageIndex >= pdfdoc.Pages.Count)
                {
                    _logger.LogWarning("Skipping invalid page {Page} for doc {DocId}", sigLoc.Page, request.DocId);
                    continue;
                }

                var pageSize = pdfdoc.Pages[pageIndex].Size;
                var x = sigLoc.XPct * pageSize.Width;
                var y = sigLoc.YPct * pageSize.Height;

                var nameLabel = sigLoc.AuthorityLevel switch
                {
                    3 => "BY AUTHORITY OF THE GOVERNOR \n\r Digitally signed by: \n\r",
                    4 => "FOR \n\r\n\r Digitally signed by: \n\r",
                    _ => "Digitally signed by: \n\r"
                };

                var cert = new Spire.Pdf.Security.PdfCertificate(certPath, pfxPassword);
                var uniqueSigName = $"Signature_{Guid.NewGuid()}";
                var signature = new Spire.Pdf.Security.PdfSignature(pdfdoc, pdfdoc.Pages[pageIndex], cert, uniqueSigName);

                pdfdoc.AllowCreateForm = pdfdoc.Form == null;

                var datePost = request.IsDisplayDate != 1
                    ? $"\n\r Date: {DateTime.Now:MMM dd, yyyy}"
                    : "";

                signature.Bounds = new System.Drawing.RectangleF(x, y, 200, 60);
                signature.GraphicsMode = Spire.Pdf.Security.GraphicMode.SignImageAndSignDetail;
                signature.NameLabel = nameLabel;
                signature.Name = $"{signFname} \n\r";
                signature.DateLabel = datePost;
                signature.DocumentPermissions = Spire.Pdf.Security.PdfCertificationFlags.AllowFormFill
                                              | Spire.Pdf.Security.PdfCertificationFlags.ForbidChanges;
                signature.SignDetailsFont = new Spire.Pdf.Graphics.PdfFont(Spire.Pdf.Graphics.PdfFontFamily.TimesRoman, 5f);
                signature.SignNameFont = new Spire.Pdf.Graphics.PdfFont(Spire.Pdf.Graphics.PdfFontFamily.Helvetica, 5f);
                signature.SignImageLayout = Spire.Pdf.Security.SignImageLayout.Stretch;

                if (sigLoc.SpecimenType == 0)
                    signature.SignImageSource = Spire.Pdf.Graphics.PdfImage.FromFile(sigImagePath);
                else
                    signature.SignImageSource = !string.IsNullOrEmpty(altImagePath)
                        ? Spire.Pdf.Graphics.PdfImage.FromFile(altImagePath)
                        : Spire.Pdf.Graphics.PdfImage.FromFile(noImagePath);

                try
                {
                    var locationSql = @"INSERT INTO bacpdfsign.dbo.document_signature_location
                            (sig_id, sign_device_name, sign_address, sign_latitude, sign_longitude, sign_accuracy,
                             sign_x, sign_y, sign_page, sign_type, sign_image, sign_datetime,
                             sign_passwod, sign_method, pfx_id, sign_is_alternate_signature)
                          VALUES
                            (@SigId, @DeviceType, @Address, @Latitude, @Longitude, @Accuracy,
                             @X, @Y, @Page, 1, '', GETDATE(),
                             @Password, @ModsId, @PfxId, @SpecimenType);
                          SELECT SCOPE_IDENTITY();";
                    var locationParams = new
                    {
                        SigId = insertSigIdForLocation,
                        DeviceType = request.DgDeviceType,
                        Address = request.DgAddress,
                        Latitude = request.DgLatitude,
                        Longitude = request.DgLongitude,
                        Accuracy = request.DgAccuracy,
                        X = x,
                        Y = y,
                        Page = sigLoc.Page,
                        Password = pfxDetails.Password ?? "",  // store encrypted, never cleartext
                        ModsId = request.ModsId,
                        PfxId = pfxDetails.PfxId,
                        SpecimenType = sigLoc.SpecimenType
                    };

                    if (alternateTx != null)
                        await _dbService.ExecuteScalarAsync<int, dynamic>(locationSql, locationParams, CommandType.Text, alternateTx);
                    else
                        await _dbService.ExecuteScalarAsync<int, dynamic>(locationSql, locationParams, CommandType.Text);
                }
                catch (Exception ec)
                {
                    if (alternateTx != null)
                        await _dbService.RollbackTransactionAsync(alternateTx);
                    await LogError(eidSignature, eidUtSignature, request.DocId,
                        ec.Message + "---fail adding p12 to pdf, saving sign details");
                    return new SaveSignatureResult { Success = false, Message = "Something went wrong" };
                }
            }

            // 9. Update signatory status and get next signatory
            int nextSignatoryEid = 0;
            int nextSignatoryUserType = 0;

            try
            {
                // closingSigId is the row being satisfied by this signature — the
                // caller's own row for a normal signature, or the PRINCIPAL's
                // original pending row (not the newly-inserted alternate row,
                // which is already status = 1) when signing as an alternate.
                if (alternateTx != null)
                {
                    // Guard against a double-submit/race on the alternate path:
                    // only close the principal's row if it's still actually
                    // pending. If a concurrent request already closed it, this
                    // pass must not also leave behind the alternate row (and
                    // its signature-location/PDF stamp) it already inserted —
                    // roll back the whole transaction, including that insert.
                    var closeAffected = await _dbService.ExecuteAsync<dynamic>(
                        "UPDATE bacpdfsign.dbo.document_signatories SET sig_status = 1 WHERE sig_id = @SignId AND sig_status = 0",
                        new { SignId = closingSigId },
                        CommandType.Text,
                        alternateTx);

                    if (closeAffected == 0)
                    {
                        await _dbService.RollbackTransactionAsync(alternateTx);
                        await LogError(eidSignature, eidUtSignature, request.DocId,
                            "Principal's signatory row was no longer pending (concurrent signature already applied)");
                        return new SaveSignatureResult { Success = false, Message = "This document is no longer pending your principal's signature." };
                    }
                }
                else
                {
                    // Symmetric guard with the alternate branch above: this row
                    // may have already been closed by a concurrent alternate
                    // signature on the same principal row (or a duplicate
                    // submit of this same request). Without the sig_status = 0
                    // filter and an affected-rows check, this UPDATE silently
                    // no-ops while the method proceeds to step 10 and re-stamps
                    // the PDF under the caller's own credentials — overwriting
                    // whatever signature (e.g. the alternate's) was already
                    // applied to the file on disk. This path has never been
                    // transactional, so there is nothing to roll back here —
                    // just stop before step 10 touches the file.
                    var closeAffected = await _dbService.ExecuteAsync<dynamic>(
                        "UPDATE bacpdfsign.dbo.document_signatories SET sig_status = 1 WHERE sig_id = @SignId AND sig_status = 0",
                        new { SignId = closingSigId },
                        CommandType.Text);

                    if (closeAffected == 0)
                    {
                        await LogError(eidSignature, eidUtSignature, request.DocId,
                            "Signatory row was no longer pending (concurrent signature already applied)");
                        return new SaveSignatureResult { Success = false, Message = "This document is no longer pending your signature." };
                    }
                }

                var statusSql = @"DECLARE @counter INT = (
                          SELECT COUNT(*) FROM bacpdfsign.dbo.document_signatories
                          WHERE doc_id = @DocId AND sig_status = 0
                      );

                      IF @counter > 0
                          UPDATE bacpdfsign.dbo.document_attach SET doc_status_id = 3 WHERE doc_id = @DocId;
                      ELSE
                          UPDATE bacpdfsign.dbo.document_attach SET doc_status_id = 2 WHERE doc_id = @DocId;

                      SELECT TOP 1 sig_eid AS SigEid, sig_user_type AS SigUserType
                      FROM bacpdfsign.dbo.document_signatories
                      WHERE doc_id = @DocId AND sig_status = 0
                        AND sig_order > (SELECT sig_order FROM bacpdfsign.dbo.document_signatories WHERE sig_id = @SignId)
                      ORDER BY sig_order ASC;";
                var statusParams = new { SignId = closingSigId, DocId = request.DocId };

                var statusResult = alternateTx != null
                    ? await _dbService.QueryFirstOrDefaultAsync<NextSignatoryDto, dynamic>(statusSql, statusParams, CommandType.Text, alternateTx)
                    : await _dbService.QueryFirstOrDefaultAsync<NextSignatoryDto, dynamic>(statusSql, statusParams, CommandType.Text);

                if (statusResult != null)
                {
                    nextSignatoryEid = statusResult.SigEid;
                    nextSignatoryUserType = statusResult.SigUserType;
                }

                if (alternateTx != null)
                    await _dbService.CommitTransactionAsync(alternateTx);
            }
            catch (Exception ec)
            {
                if (alternateTx != null)
                    await _dbService.RollbackTransactionAsync(alternateTx);
                await LogError(eidSignature, eidUtSignature, request.DocId,
                    ec.Message + "---fail to update signatory to signed");
                return new SaveSignatureResult { Success = false, Message = "Something went wrong" };
            }

            // 10. Save signed PDF to NAS with retry
            try
            {
                var yearFolder = (DateTime.Now.Year % 100).ToString();
                var targetDir = Path.Combine(DigitalSignaturePath, yearFolder, $"Form{request.DocId}");
                var destFile = Path.Combine(targetDir, "file.pdf");
                const int maxRetries = 3;
                const int delayMs = 2000;

                using (_fileStorage.Connect(DigitalSignaturePath))
                {
                    var saved = false;
                    var attempt = 0;

                    if (_fileStorage.Mode == "S3")
                    {
                        using var ms = new MemoryStream();
                        pdfdoc.SaveToStream(ms);
                        await _fileStorage.WriteFileAsync(destFile, ms.ToArray());
                        saved = true;
                    }
                    else
                    {
                        if (!Directory.Exists(targetDir))
                            Directory.CreateDirectory(targetDir);

                        while (!saved && attempt < maxRetries)
                        {
                            try
                            {
                                attempt++;
                                var tempFile = Path.Combine(targetDir, $"temp_{Guid.NewGuid()}.pdf");
                                pdfdoc.SaveToFile(tempFile);

                                if (System.IO.File.Exists(destFile))
                                    System.IO.File.Delete(destFile);

                                System.IO.File.Move(tempFile, destFile);
                                saved = true;
                            }
                            catch (IOException ioEx) when (attempt < maxRetries)
                            {
                                _logger.LogWarning(ioEx, "Retry {Attempt}/{Max} saving PDF to NAS for doc: {DocId}",
                                    attempt, maxRetries, request.DocId);
                                await Task.Delay(delayMs);
                            }
                        }
                    }

                    if (!saved)
                    {
                        await LogError(eidSignature, eidUtSignature, request.DocId, "Max retries exceeded saving PDF to NAS");
                        return new SaveSignatureResult { Success = false, Message = "Something went wrong while saving the file to NAS." };
                    }
                }
            }
            finally
            {
                pdfdoc.Close();
            }

            // 11. Execute trigger query (if any)
            try
            {
                var triggerQuery = await _dbService.ExecuteScalarAsync<string, dynamic>(
                    @"SELECT ISNULL(
                        (SELECT TOP 1 sig_query_signed FROM bacpdfsign.dbo.document_signatories WHERE sig_id = @SignId),
                        '')",
                    new { SignId = closingSigId },
                    CommandType.Text);

                if (!string.IsNullOrEmpty(triggerQuery))
                    await _dbService.ExecuteAsync<dynamic>(triggerQuery, new { }, CommandType.Text);
            }
            catch (Exception ex)
            {
                await LogError(eidSignature, eidUtSignature, request.DocId,
                    ex.Message + "---fail to execute signed query addition");
                return new SaveSignatureResult { Success = false, Message = "Something went wrong" };
            }

            // 12. Return success
            return new SaveSignatureResult
            {
                Success = true,
                DocId = request.DocId,
                DocCode = docDetails.DocCode,
                NextSignatoryEid = nextSignatoryEid,
                NextSignatoryUserType = nextSignatoryUserType,
                SignerEid = request.Eid,
                SignerUserType = request.UserType,
                LegacyResult = $"{request.DocId},Document,{nextSignatoryEid},{docDetails.DocCode},{nextSignatoryUserType},{request.Eid},{request.UserType}"
            };
        }
        catch (Exception ex)
        {
            // An exception between opening alternateTx (step 8a) and its commit
            // (step 9) can originate outside those steps' own try/catch blocks —
            // e.g. Spire/PDF work in the signing loop — and must not leave the
            // transaction dangling open.
            if (alternateTx != null)
            {
                try { await _dbService.RollbackTransactionAsync(alternateTx); } catch { /* already rolled back or connection gone */ }
            }
            _logger.LogError(ex, "Error saving signature image for doc: {DocId}, eid: {Eid}", request.DocId, request.Eid);
            return new SaveSignatureResult
            {
                Success = false,
                IsServerError = true,
                Message = "Something went wrong",
                // TEMP DEBUG (remove once the local save_signature_image 500 is
                // diagnosed): surfaces the real exception to the browser console
                // via the response body. Development-only — see DebugError doc comment.
                DebugError = _env.IsDevelopment() ? ex.ToString() : null
            };
        }
    }

    // ==================== New: ownership-check helpers for the public-sign flow ====================

    public async Task<PendingDocumentDto?> GetPendingDocumentByIdAsync(int docId, int eid, int userType)
    {
        var sql = @"
            SELECT
                da.doc_id           AS DocId,
                da.doc_name         AS DocName,
                da.doc_code         AS DocCode,
                da.doc_description  AS DocDescription,
                da.doc_datetime     AS DocCreatedDatetime,
                da.doc_eid          AS DocEid,
                da.doc_eid_user_type AS DocEidUserType,
                da.doc_status_id    AS DocStatusId,
                rs.status_type      AS DocStatusName,
                dt.document_description AS DocTypeName,
                dt.document_abbr    AS DocTypeAbbr,
                ds.sig_id           AS SigId,
                ds.sig_order        AS SigOrder,
                ds.sig_eid          AS SigEid,
                ds.sig_user_type    AS SigUserType,
                ds.sig_status       AS SigStatus,
                rss.description     AS SigStatusName,
                ds.date_time_inserted AS SignatoryAssignedDatetime,
                ISNULL(ds.sig_level, 0) AS SigLevel,
                ISNULL(ds.sig_sign_count, 0) AS SigSignCount
            FROM [bacpdfsign].[dbo].[document_signatories] ds
            INNER JOIN [bacpdfsign].[dbo].[document_attach] da
                ON ds.doc_id = da.doc_id
            INNER JOIN [bacpdfsign].[dbo].[document_types] dt
                ON da.doc_type_id = dt.id
            INNER JOIN [bacpdfsign].[dbo].[req_status] rs
                ON da.doc_status_id = rs.id
            INNER JOIN [bacpdfsign].[dbo].[req_sign_status] rss
                ON ds.sig_status = rss.id
            WHERE ds.doc_id = @DocId
              AND ds.sig_eid = @Eid
              AND ds.sig_user_type = @UserType
              AND ds.sig_status = 0
              AND da.doc_status_id IN (1, 3)
              AND NOT EXISTS (
                  SELECT 1
                  FROM [bacpdfsign].[dbo].[document_signatories] ds2
                  WHERE ds2.doc_id = ds.doc_id
                    AND ds2.sig_order < ds.sig_order
                    AND ds2.sig_status != 1
              )";

        var core = await _dbService.QueryFirstOrDefaultAsync<PendingDocumentCoreDto, dynamic>(
            sql, new { DocId = docId, Eid = eid, UserType = userType }, CommandType.Text);

        if (core == null)
            return null;

        var dto = new PendingDocumentDto
        {
            DocId = core.DocId,
            DocName = core.DocName,
            DocCode = core.DocCode,
            DocDescription = core.DocDescription,
            DocCreatedDatetime = core.DocCreatedDatetime,
            DocStatusId = core.DocStatusId,
            DocStatusName = core.DocStatusName,
            DocTypeName = core.DocTypeName,
            DocTypeAbbr = core.DocTypeAbbr,
            SigId = core.SigId,
            SigOrder = core.SigOrder,
            SigEid = core.SigEid,
            SigUserType = core.SigUserType,
            SigStatus = core.SigStatus,
            SigStatusName = core.SigStatusName,
            SignatoryAssignedDatetime = core.SignatoryAssignedDatetime,
            CurrentSignatureDatetime = null,
            SigLevel = core.SigLevel,
            SigSignCount = core.SigSignCount
        };

        var sqlNames = @"
            SELECT eid AS Eid, user_type AS UserType, fname AS Fname,
                   position AS Position, office_name AS OfficeName
            FROM [bacpdfsign].[dbo].[signatory_names]
            WHERE eid IN @Eids";

        var eids = new[] { core.DocEid, core.SigEid }.Distinct().ToList();
        var names = (await _dbService.QueryAsync<SignatoryNameDto, dynamic>(
            sqlNames, new { Eids = eids }, CommandType.Text)).ToList();
        var nameMap = names.ToDictionary(n => (n.Eid, n.UserType), n => n);

        if (nameMap.TryGetValue((core.DocEid, core.DocEidUserType), out var creator))
        {
            dto.DocCreatedByName = creator.Fname;
            dto.DocCreatedByPosition = creator.Position;
            dto.DocCreatedByOffice = creator.OfficeName;
        }

        if (nameMap.TryGetValue((core.SigEid, core.SigUserType), out var signatory))
        {
            dto.SignatoryName = signatory.Fname;
            dto.SignatoryPosition = signatory.Position;
            dto.SignatoryOffice = signatory.OfficeName;
        }

        return dto;
    }

    public async Task<bool> IsSignatoryOnDocumentAsync(int docId, int eid, int userType)
    {
        var sql = @"
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM [bacpdfsign].[dbo].[document_signatories]
                WHERE doc_id = @DocId AND sig_eid = @Eid AND sig_user_type = @UserType
            ) THEN 1 ELSE 0 END";

        var exists = await _dbService.ExecuteScalarAsync<int, dynamic>(
            sql, new { DocId = docId, Eid = eid, UserType = userType }, CommandType.Text);

        return exists == 1;
    }

    // Lowest sig_order (ties broken by lowest sig_id) — the signatory who can
    // actually sign right away, whether the document is sequential or
    // simultaneous. Used to auto-mint a ready-to-sign public link right after
    // upload. Null if the document has no signatories.
    public async Task<(int Eid, int UserType)?> GetFirstSignatoryAsync(int docId)
    {
        var row = await _dbService.QueryFirstOrDefaultAsync<FirstSignatoryDto, dynamic>(
            @"SELECT TOP 1 sig_eid AS Eid, sig_user_type AS UserType
              FROM bacpdfsign.dbo.document_signatories
              WHERE doc_id = @DocId
              ORDER BY sig_order ASC, sig_id ASC",
            new { DocId = docId },
            CommandType.Text);

        return row == null ? null : (row.Eid, row.UserType);
    }

    // ==================== Private Helper Methods (moved from DGSignController) ====================

    private async Task<string> GetSigId(string docId, string eid, string userType)
    {
        return await _dbService.ExecuteScalarAsync<string, dynamic>(
            @"SELECT TOP 1 CAST(sig_id AS VARCHAR) FROM bacpdfsign.dbo.document_signatories
              WHERE doc_id = @DocId AND sig_eid = @Eid AND sig_user_type = @UserType AND sig_status = 0",
            new { DocId = docId, Eid = eid, UserType = userType },
            CommandType.Text) ?? "";
    }

    private async Task<string> GetSigFname(string eid, string userType)
    {
        return await _dbService.ExecuteScalarAsync<string, dynamic>(
            @"SELECT TOP 1 fname FROM bacpdfsign.dbo.signatory_names WHERE eid = @Eid AND user_type = @UserType",
            new { Eid = eid, UserType = userType },
            CommandType.Text) ?? "";
    }

    private async Task<long> GetAlterSpecimen(string eid, string userType)
    {
        return await _dbService.ExecuteScalarAsync<long, dynamic>(
            @"SELECT ISNULL((SELECT TOP 1 id FROM bacpdfsign.dbo.pfx_initial_signature
              WHERE eid = @Eid AND user_type = @UserType AND active = 1), 0)",
            new { Eid = eid, UserType = userType },
            CommandType.Text);
    }

    private async Task<string> GetSigIdDesignated(string docId, string eid, string userType, string vwEids, string vwUserType)
    {
        return await _dbService.ExecuteScalarAsync<string, dynamic>(
            @"SELECT TOP 1 CAST(sig_id AS VARCHAR) FROM bacpdfsign.dbo.document_signatories
              WHERE doc_id = @DocId AND sig_eid = @VwEids AND sig_user_type = @VwUserType AND sig_status = 0",
            new { DocId = docId, Eid = eid, UserType = userType, VwEids = vwEids, VwUserType = vwUserType },
            CommandType.Text) ?? "";
    }

    private async Task<string> GetSigIdAlternate(string docId, string eid, string userType, string vwEids, string vwUserType)
    {
        return await _dbService.ExecuteScalarAsync<string, dynamic>(
            @"SELECT TOP 1 CAST(sig_id AS VARCHAR) FROM bacpdfsign.dbo.document_signatories
              WHERE doc_id = @DocId AND sig_eid = @VwEids AND sig_user_type = @VwUserType AND sig_status = 0",
            new { DocId = docId, Eid = eid, UserType = userType, VwEids = vwEids, VwUserType = vwUserType },
            CommandType.Text) ?? "";
    }

    // Fetches the full row (not just the id) for the principal's pending
    // document_signatories row an alternate/delegate signature is about to
    // satisfy — used to clone it into a new row under the alternate's own
    // identity in SaveSignatureImageAsync. Returns null for an empty/unknown
    // sigId (e.g. GetSigIdAlternate/GetSigIdDesignated found no pending row).
    private async Task<PrincipalSignatoryRowDto?> GetSignatoryRowForClosureAsync(string sigId)
    {
        if (string.IsNullOrEmpty(sigId)) return null;

        return await _dbService.QueryFirstOrDefaultAsync<PrincipalSignatoryRowDto, dynamic>(
            @"SELECT doc_id AS DocId, sig_code AS SigCode, sig_order AS SigOrder,
                     ISNULL(sig_level, 0) AS SigLevel, ISNULL(sig_sign_count, 1) AS SigSignCount,
                     sig_query_signed AS SigQuerySigned, sig_query_return AS SigQueryReturn
              FROM bacpdfsign.dbo.document_signatories WHERE sig_id = @SigId",
            new { SigId = sigId },
            CommandType.Text);
    }

    // Re-checked at the moment of signing (not just when listing pending
    // documents) so save_signature_image can never be tricked into signing
    // "as an alternate" for a relationship that isn't currently active, no
    // matter what a client sends in isAlternate/vwEids/vwUserType. Mirrors
    // AlternateSignatoriesController.IsActiveAlternateForAsync's predicate —
    // the two are intentionally duplicated across the CRUD layer and this
    // enforcement layer rather than shared, since they serve different
    // callers, but any change to the "what counts as active" rule must be
    // applied to both.
    private async Task<bool> IsActiveAlternateForSigningAsync(int principalEid, int principalUserType, int alternateEid, int alternateUserType)
    {
        var count = await _dbService.ExecuteScalarAsync<int, dynamic>(
            @"SELECT COUNT(1) FROM bacpdfsign.dbo.alternate_signatories
              WHERE user_eid = @PrincipalEid AND user_type = @PrincipalUserType
                AND user_alternate_eid = @AlternateEid AND user_alternate_type = @AlternateUserType
                AND isactive = 1
                AND (is_permanent = 1 OR (CAST(GETDATE() AS DATE) BETWEEN dateFrom AND dateTo))",
            new { PrincipalEid = principalEid, PrincipalUserType = principalUserType, AlternateEid = alternateEid, AlternateUserType = alternateUserType },
            CommandType.Text);
        return count > 0;
    }

    private async Task<bool> IsDocTypeAllowedForAlternateAsync(int principalEid, int principalUserType, int alternateEid, int alternateUserType, long docTypeId)
    {
        var count = await _dbService.ExecuteScalarAsync<int, dynamic>(
            @"SELECT COUNT(1)
              FROM bacpdfsign.dbo.alternate_signatories_documents asd
              INNER JOIN bacpdfsign.dbo.alternate_signatories a ON a.id = asd.alter_id
              WHERE a.user_eid = @PrincipalEid AND a.user_type = @PrincipalUserType
                AND a.user_alternate_eid = @AlternateEid AND a.user_alternate_type = @AlternateUserType
                AND asd.doc_type_id = @DocTypeId
                AND a.isactive = 1
                AND (a.is_permanent = 1 OR (CAST(GETDATE() AS DATE) BETWEEN a.dateFrom AND a.dateTo))",
            new { PrincipalEid = principalEid, PrincipalUserType = principalUserType, AlternateEid = alternateEid, AlternateUserType = alternateUserType, DocTypeId = docTypeId },
            CommandType.Text);
        return count > 0;
    }

    private async Task<string?> GetAlternateSignatureImage(string eidSignature, string eidUtSignature, long isAltAvailable)
    {
        if (isAltAvailable == 0) return null;

        try
        {
            var altDetails = await _dbService.QueryFirstOrDefaultAsync<dynamic, dynamic>(
                @"SELECT code, initial_signature FROM bacpdfsign.dbo.pfx_initial_signature
                  WHERE eid = @Eid AND user_type = @Ut AND active = 1",
                new { Eid = eidSignature, Ut = eidUtSignature },
                CommandType.Text);

            if (altDetails == null) return null;

            // ── Try NAS path first (same location as get_signature_image_merged type=initial) ──
            var credentials = _credentialService.GetNetworkCredential();
            var networkPath = _fileStorage.RootPath;
            var nasEidFolder = Path.Combine(DigitalSpicimenPath, eidUtSignature, eidSignature);
            var nasInitialPng = Path.Combine(nasEidFolder, $"initial_{eidSignature}.png");

            using (_fileStorage.Connect(networkPath))
            {
                if (!await _fileStorage.FileExistsAsync(nasInitialPng))
                {
                    // Write to NAS from DB
                    if (altDetails.initial_signature is byte[] sigBytes)
                    {
                        await _fileStorage.WriteFileAsync(nasInitialPng, sigBytes);
                    }
                    else
                    {
                        var sigBase64 = altDetails.initial_signature?.ToString() ?? "";
                        sigBase64 = Regex.Replace(sigBase64, @"^[\w/\:.-]+;base64,", string.Empty);
                        if (!string.IsNullOrEmpty(sigBase64))
                            await _fileStorage.WriteFileAsync(nasInitialPng, Convert.FromBase64String(sigBase64));
                    }
                }

                if (await _fileStorage.FileExistsAsync(nasInitialPng))
                    return nasInitialPng;
            }

            // ── Fallback: local temp folder ──
            string code = altDetails.code?.ToString() ?? "";
            var altDir = Path.Combine(_env.ContentRootPath, "Data", "upload",
                $"temp_folder{eidSignature}", "info", $"signature_folder{code}");
            var altPath = Path.Combine(altDir, $"initial_{eidSignature}.png");

            if (!System.IO.File.Exists(altPath))
            {
                if (!Directory.Exists(altDir))
                    Directory.CreateDirectory(altDir);

                if (altDetails.initial_signature is byte[] sigBytes2)
                {
                    await System.IO.File.WriteAllBytesAsync(altPath, sigBytes2);
                }
                else
                {
                    var sigBase64 = altDetails.initial_signature?.ToString() ?? "";
                    sigBase64 = Regex.Replace(sigBase64, @"^[\w/\:.-]+;base64,", string.Empty);
                    await System.IO.File.WriteAllBytesAsync(altPath, Convert.FromBase64String(sigBase64));
                }
            }

            return altPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get alternate signature image for eid: {Eid}", eidSignature);
            return null;
        }
    }

    // ==================== SAVE DOCUMENT (UPLOAD) ====================

    private static string GenerateAlnumCode(int length = 16)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(RandomNumberGenerator.GetItems<char>(chars, length));
    }

    // Valid signatory_leveltype ids (id 2 is deliberately excluded — see
    // ReferencesController.GetSignatoryLevels). Anything else — null, 2, or
    // garbage — falls back to 1 ("for signature", the plain default stamp).
    private static readonly HashSet<int> ValidSigLevels = new() { 1, 3, 4 };
    private static int NormalizeSigLevel(int? level) => level.HasValue && ValidSigLevels.Contains(level.Value) ? level.Value : 1;

    // document_attach.doc_type — which storage method actually holds the PDF
    // bytes. See document_savetype: 0=Binary (doc_attachement, legacy
    // DGSign) 1=NAS 2=Local 3=Custom path 4=MinIO. 0 is never written by this
    // app (always file-based going forward) — only reachable on old rows.
    private static int MapStorageModeToDocType(string mode) => mode switch
    {
        "Nas" => 1,
        "Local" => 2,
        "Custom" => 3,
        "S3" => 4,
        _ => 1
    };

    /// <summary>
    /// Writes bytes to file storage: direct write when Mode == "S3",
    /// otherwise a temp-file + atomic move into targetDirFull with retries
    /// (handles transient NAS/Local hiccups). Shared by SaveDocumentAsync
    /// and the lazy binary→file migration in
    /// MigrateLegacyBinaryIfNeededAsync.
    /// </summary>
    private async Task<(bool Saved, bool CreatedDir)> WriteFileWithRetryAsync(string destFile, byte[] bytes, string targetDirFull)
    {
        if (_fileStorage.Mode == "S3")
        {
            await _fileStorage.WriteFileAsync(destFile, bytes);
            return (true, false);
        }

        var createdDir = false;
        if (!Directory.Exists(targetDirFull))
        {
            Directory.CreateDirectory(targetDirFull);
            createdDir = true;
        }

        const int maxRetries = 3;
        const int delayMs = 2000;
        var saved = false;
        var attempt = 0;

        while (!saved && attempt < maxRetries)
        {
            try
            {
                attempt++;
                var tempFile = Path.Combine(targetDirFull, $"temp_{Guid.NewGuid()}.pdf");
                await System.IO.File.WriteAllBytesAsync(tempFile, bytes);

                if (System.IO.File.Exists(destFile))
                    System.IO.File.Delete(destFile);

                System.IO.File.Move(tempFile, destFile);
                saved = true;
            }
            catch (IOException ioEx) when (attempt < maxRetries)
            {
                _logger.LogWarning(ioEx, "Retry {Attempt}/{Max} saving file to {DestFile}", attempt, maxRetries, destFile);
                await Task.Delay(delayMs);
            }
        }

        return (saved, createdDir);
    }

    public async Task<SaveDocumentResult> SaveDocumentAsync(
        UploadDocumentMetaDto meta,
        IFormFile? pdfFile,
        List<IFormFile>? supportingFiles)
    {
        // 1. Validate before touching the DB or filesystem.
        if (pdfFile == null && (string.IsNullOrWhiteSpace(meta.DocDirectory) || string.IsNullOrWhiteSpace(meta.DocName)))
        {
            return new SaveDocumentResult
            {
                Success = false,
                Message = "docDirectory and docName are required when no PDF file is uploaded."
            };
        }
        if (meta.DocEid <= 0)
        {
            return new SaveDocumentResult { Success = false, Message = "docEid is required." };
        }

        IDbTransaction? tx = null;
        string? targetDirFull = null;
        bool createdDir = false;

        try
        {
            // 2. Generate a unique doc_code up front (doesn't need doc_id).
            string docCode = "";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var candidate = GenerateAlnumCode();
                var count = await _dbService.ExecuteScalarAsync<int, dynamic>(
                    "SELECT COUNT(*) FROM bacpdfsign.dbo.document_attach WHERE doc_code = @Code",
                    new { Code = candidate },
                    CommandType.Text);
                if (count == 0)
                {
                    docCode = candidate;
                    break;
                }
            }
            if (string.IsNullOrEmpty(docCode))
            {
                _logger.LogError("Failed to generate a unique doc_code after 5 attempts");
                return new SaveDocumentResult { Success = false, IsServerError = true, Message = "Something went wrong" };
            }

            tx = await _dbService.BeginTransactionAsync();

            // 3. Insert document_attach with a placeholder directory, get the new doc_id.
            var docId = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"INSERT INTO bacpdfsign.dbo.document_attach
                    (doc_name, doc_type, doc_attachement, doc_directory, doc_description,
                     doc_status_id, doc_designated, doc_datetime, doc_eid, doc_datetime_update,
                     doc_code, doc_signatory_type, doc_is, doc_type_id, doc_eid_user_type,
                     doc_pages, doc_transaction)
                  OUTPUT INSERTED.doc_id
                  VALUES
                    ('', @DocType, NULL, '', @DocDescription,
                     1, @DocDesignated, CONVERT(NVARCHAR(50), GETDATE(), 100), @DocEid, NULL,
                     @DocCode, 1, @DocIs, @DocTypeId, @DocEidUserType,
                     0, @DocTransaction);",
                new
                {
                    // pdfFile present -> we're about to physically write it via
                    // _fileStorage, so record whichever backend is actually
                    // active right now. No binary -> caller already placed it
                    // on NAS out-of-band (see the pdfFile==null validation
                    // above), so it stays 1 regardless of our own active mode.
                    DocType = pdfFile != null ? MapStorageModeToDocType(_fileStorage.Mode) : 1,
                    DocDescription = meta.DocTitle ?? "",
                    DocDesignated = (object?)meta.DocDesignated ?? DBNull.Value,
                    DocEid = meta.DocEid,
                    DocCode = docCode,
                    DocIs = (object?)meta.DocIs ?? DBNull.Value,
                    DocTypeId = meta.DocTypeId ?? 1,
                    DocEidUserType = meta.DocEidUserType,
                    DocTransaction = (object?)meta.DocTransaction ?? DBNull.Value
                },
                CommandType.Text,
                tx);

            // 4. Compute the canonical folder (relative to RootPath, matching
            // how GetOriginalPdfBytes/existing reads combine RootPath + DocDirectory + DocName).
            var yearFolder = (DateTime.Now.Year % 100).ToString();
            var digSigLeaf = Path.GetFileName(DigitalSignaturePath.TrimEnd(Path.DirectorySeparatorChar));
            targetDirFull = Path.Combine(DigitalSignaturePath, yearFolder, $"Form{docId}");
            var docDirectoryRelative = Path.Combine(digSigLeaf, yearFolder, $"Form{docId}");

            string finalDocName;
            string finalDocDirectory;
            int pageCount;

            if (pdfFile != null)
            {
                // 5. Binary present — save to NAS ourselves. On-disk filename is
                // always "file.pdf" (matches SaveSignatureImageAsync's convention
                // and GetOriginalPdfBytes's Path.Combine(DocDirectory, DocName) read).
                using (_fileStorage.Connect(DigitalSignaturePath))
                {
                    using var ms = new MemoryStream();
                    await pdfFile.CopyToAsync(ms);
                    var pdfBytes = ms.ToArray();

                    try
                    {
                        using var pdfdoc = new Spire.Pdf.PdfDocument();
                        pdfdoc.LoadFromBytes(pdfBytes);
                        pageCount = pdfdoc.Pages.Count;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load uploaded PDF to count pages for new doc");
                        pageCount = 0;
                    }

                    var destFile = Path.Combine(targetDirFull, "file.pdf");
                    var (saved, createdDirNow) = await WriteFileWithRetryAsync(destFile, pdfBytes, targetDirFull);
                    if (createdDirNow) createdDir = true;

                    if (!saved)
                    {
                        await _dbService.RollbackTransactionAsync(tx);
                        return new SaveDocumentResult { Success = false, IsServerError = true, Message = "Something went wrong while saving the file to NAS." };
                    }
                }

                finalDocName = "file.pdf";
                finalDocDirectory = docDirectoryRelative;
            }
            else
            {
                // 6. Binary absent — trust the caller's own location as given.
                finalDocName = meta.DocName!;
                finalDocDirectory = meta.DocDirectory!;
                pageCount = meta.DocPages ?? 0;
            }

            // 7. Persist the real directory/name/pages now that we know them.
            await _dbService.ExecuteAsync<dynamic>(
                @"UPDATE bacpdfsign.dbo.document_attach
                  SET doc_directory = @DocDirectory, doc_name = @DocName, doc_pages = @DocPages
                  WHERE doc_id = @DocId",
                new { DocDirectory = finalDocDirectory, DocName = finalDocName, DocPages = pageCount, DocId = docId },
                CommandType.Text,
                tx);

            // 8. Insert one document_signatories row per signatory.
            var order = 1;
            foreach (var sig in meta.Signatories)
            {
                await _dbService.ExecuteAsync<dynamic>(
                    @"INSERT INTO bacpdfsign.dbo.document_signatories
                        (doc_id, sig_code, sig_eid, sig_status, sig_order, sig_remarks,
                         sig_query_signed, sig_query_return, sig_user_type, sig_level,
                         sig_sign_count, sig_remarks_datenTime, date_time_inserted)
                      VALUES
                        (@DocId, @SigCode, @SigEid, 0, @SigOrder, '',
                         @QuerySigned, @QueryReturn, @SigUserType, @SigLevel,
                         @SigSignCount, '', CONVERT(NVARCHAR(50), GETDATE(), 100));",
                    new
                    {
                        DocId = docId,
                        SigCode = docCode,
                        SigEid = sig.Eid,
                        SigOrder = sig.Order > 0 ? sig.Order : order,
                        QuerySigned = (object?)sig.QuerySigned ?? DBNull.Value,
                        QueryReturn = (object?)sig.QueryReturn ?? DBNull.Value,
                        SigUserType = sig.UserType,
                        SigLevel = NormalizeSigLevel(sig.Level),
                        SigSignCount = sig.NumSignatures > 0 ? sig.NumSignatures : 1
                    },
                    CommandType.Text,
                    tx);
                order++;
            }

            // 9. Supporting files — always written by this call itself against
            // the canonical folder, regardless of which PDF condition applied.
            var supportingCount = 0;
            if (supportingFiles != null && supportingFiles.Count > 0)
            {
                var supportDirFull = Path.Combine(targetDirFull, "support");
                var supportDirRelative = Path.Combine(docDirectoryRelative, "support");

                using (_fileStorage.Connect(DigitalSignaturePath))
                {
                    foreach (var file in supportingFiles)
                    {
                        var sanitizedName = Path.GetFileName(file.FileName);
                        var destFile = Path.Combine(supportDirFull, sanitizedName);

                        var (saved, writeError) = await WriteSupportingFileWithRetryAsync(destFile, file);
                        if (!saved)
                        {
                            await _dbService.RollbackTransactionAsync(tx);
                            return new SaveDocumentResult { Success = false, IsServerError = true, Message = writeError ?? $"Something went wrong while saving supporting file '{sanitizedName}'." };
                        }

                        await _dbService.ExecuteAsync<dynamic>(
                            @"INSERT INTO bacpdfsign.dbo.document_support
                                (sup_doc_name, sup_code, sup_document, sup_type, sup_datetme, sup_eid, doc_id, sup_location)
                              VALUES
                                (@Name, @Code, NULL, @Type, CONVERT(NVARCHAR(50), GETDATE(), 100), @Eid, @DocId, @Location);",
                            new
                            {
                                Name = sanitizedName,
                                Code = docCode,
                                Type = file.ContentType,
                                Eid = meta.DocEid,
                                DocId = docId,
                                Location = Path.Combine(supportDirRelative, sanitizedName)
                            },
                            CommandType.Text,
                            tx);

                        supportingCount++;
                    }
                }
            }

            await _dbService.CommitTransactionAsync(tx);

            return new SaveDocumentResult
            {
                Success = true,
                DocId = docId,
                DocCode = docCode,
                DocName = finalDocName,
                DocDirectory = finalDocDirectory,
                DocPages = pageCount,
                SignatoryCount = meta.Signatories.Count,
                SupportingFileCount = supportingCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save uploaded document for eid: {Eid}", meta.DocEid);
            if (tx != null)
            {
                try { await _dbService.RollbackTransactionAsync(tx); } catch { /* already rolled back or connection gone */ }
            }
            if (createdDir && targetDirFull != null)
            {
                try { await _fileStorage.DeleteDirectoryAsync(targetDirFull); } catch { /* best-effort cleanup */ }
            }
            return new SaveDocumentResult { Success = false, IsServerError = true, Message = "Something went wrong" };
        }
    }

    // ==================== EDIT / DELETE DOCUMENT ====================

    public async Task<DocumentEditViewResult?> GetDocumentForEditAsync(int docId)
    {
        var viewResult = await GetDocumentViewAsync(docId);
        if (viewResult?.Document == null)
            return null;

        var supporting = (await _dbService.QueryAsync<DocumentSupportViewDto, dynamic>(
            @"SELECT sup_id AS SupId, doc_id AS DocId, sup_doc_name AS SupDocName,
                     sup_type AS SupType, sup_datetme AS SupDatetme, sup_location AS SupLocation
              FROM [bacpdfsign].[dbo].[document_support]
              WHERE doc_id = @DocId
              ORDER BY sup_id",
            new { DocId = docId },
            CommandType.Text)).ToList();

        return new DocumentEditViewResult
        {
            Document = viewResult.Document,
            Signatories = viewResult.Signatories,
            SupportingDocuments = supporting,
            CanEditMain = viewResult.Signatories.Count == 0 || viewResult.Signatories.All(s => s.SigStatus == 0)
        };
    }

    public async Task<UpdateDocumentResult> UpdateDocumentAsync(
        int docId,
        UploadDocumentMetaDto meta,
        IFormFile? pdfFile)
    {
        try
        {
            // 1. Lock check, before any writes — locked as soon as any
            // signatory has actually signed.
            var sigStatuses = (await _dbService.QueryAsync<int, dynamic>(
                "SELECT sig_status FROM bacpdfsign.dbo.document_signatories WHERE doc_id = @DocId",
                new { DocId = docId },
                CommandType.Text)).ToList();

            if (sigStatuses.Any(s => s != 0))
            {
                return new UpdateDocumentResult
                {
                    Success = false,
                    IsLocked = true,
                    Message = "Cannot edit — this document has signatories who have already signed. Delete and re-upload instead."
                };
            }

            // 2. Confirm the doc exists and get its current file location/code —
            // never regenerate doc_code or recompute the folder from year+docId,
            // a condition-2 upload can have an arbitrary caller-supplied directory.
            var existing = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachDetails, dynamic>(
                @"SELECT doc_id AS DocId, doc_directory AS DocDirectory, doc_name AS DocName,
                         doc_code AS DocCode, doc_pages AS DocPages
                  FROM bacpdfsign.dbo.document_attach WHERE doc_id = @DocId",
                new { DocId = docId },
                CommandType.Text);

            if (existing == null)
                return new UpdateDocumentResult { Success = false, Message = "Document not found." };

            var tx = await _dbService.BeginTransactionAsync();
            try
            {
                // 3. Editable metadata fields.
                await _dbService.ExecuteAsync<dynamic>(
                    @"UPDATE bacpdfsign.dbo.document_attach
                      SET doc_description = @DocDescription, doc_type_id = @DocTypeId,
                          doc_eid_user_type = @DocEidUserType,
                          doc_datetime_update = CONVERT(NVARCHAR(50), GETDATE(), 100)
                      WHERE doc_id = @DocId",
                    new
                    {
                        DocDescription = meta.DocTitle ?? "",
                        DocTypeId = meta.DocTypeId ?? 1,
                        DocEidUserType = meta.DocEidUserType,
                        DocId = docId
                    },
                    CommandType.Text,
                    tx);

                // 4. Replace signatories wholesale — safe because step 1 already
                // guaranteed none of the existing rows had signed, so there's no
                // real signature history under those sig_ids to lose.
                await _dbService.ExecuteAsync<dynamic>(
                    "DELETE FROM bacpdfsign.dbo.document_signatories WHERE doc_id = @DocId",
                    new { DocId = docId },
                    CommandType.Text,
                    tx);

                var order = 1;
                foreach (var sig in meta.Signatories)
                {
                    await _dbService.ExecuteAsync<dynamic>(
                        @"INSERT INTO bacpdfsign.dbo.document_signatories
                            (doc_id, sig_code, sig_eid, sig_status, sig_order, sig_remarks,
                             sig_query_signed, sig_query_return, sig_user_type, sig_level,
                             sig_sign_count, sig_remarks_datenTime, date_time_inserted)
                          VALUES
                            (@DocId, @SigCode, @SigEid, 0, @SigOrder, '',
                             @QuerySigned, @QueryReturn, @SigUserType, @SigLevel,
                             @SigSignCount, '', CONVERT(NVARCHAR(50), GETDATE(), 100));",
                        new
                        {
                            DocId = docId,
                            SigCode = existing.DocCode,
                            SigEid = sig.Eid,
                            SigOrder = sig.Order > 0 ? sig.Order : order,
                            QuerySigned = (object?)sig.QuerySigned ?? DBNull.Value,
                            QueryReturn = (object?)sig.QueryReturn ?? DBNull.Value,
                            SigUserType = sig.UserType,
                            SigLevel = NormalizeSigLevel(sig.Level),
                            SigSignCount = sig.NumSignatures > 0 ? sig.NumSignatures : 1
                        },
                        CommandType.Text,
                        tx);
                    order++;
                }

                // 5. Optionally replace the PDF — writes into the EXISTING
                // directory/name, never a recomputed one.
                var pageCount = existing.DocPages ?? 0;
                if (pdfFile != null)
                {
                    var targetDirFull = Path.Combine(NetworkPath, existing.DocDirectory ?? "");
                    using (_fileStorage.Connect(NetworkPath))
                    {
                        using var ms = new MemoryStream();
                        await pdfFile.CopyToAsync(ms);
                        var pdfBytes = ms.ToArray();

                        try
                        {
                            using var pdfdoc = new Spire.Pdf.PdfDocument();
                            pdfdoc.LoadFromBytes(pdfBytes);
                            pageCount = pdfdoc.Pages.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load replacement PDF to count pages for doc {DocId}", docId);
                            pageCount = 0;
                        }

                        var destFile = Path.Combine(targetDirFull, string.IsNullOrWhiteSpace(existing.DocName) ? "file.pdf" : existing.DocName);
                        const int maxRetries = 3;
                        const int delayMs = 2000;
                        var saved = false;
                        var attempt = 0;

                        if (_fileStorage.Mode == "S3")
                        {
                            await _fileStorage.WriteFileAsync(destFile, pdfBytes);
                            saved = true;
                        }
                        else
                        {
                            if (!Directory.Exists(targetDirFull))
                                Directory.CreateDirectory(targetDirFull);

                            while (!saved && attempt < maxRetries)
                            {
                                try
                                {
                                    attempt++;
                                    var tempFile = Path.Combine(targetDirFull, $"temp_{Guid.NewGuid()}.pdf");
                                    await System.IO.File.WriteAllBytesAsync(tempFile, pdfBytes);

                                    if (System.IO.File.Exists(destFile))
                                        System.IO.File.Delete(destFile);

                                    System.IO.File.Move(tempFile, destFile);
                                    saved = true;
                                }
                                catch (IOException ioEx) when (attempt < maxRetries)
                                {
                                    _logger.LogWarning(ioEx, "Retry {Attempt}/{Max} saving replacement PDF for doc {DocId}", attempt, maxRetries, docId);
                                    await Task.Delay(delayMs);
                                }
                            }
                        }

                        if (!saved)
                        {
                            await _dbService.RollbackTransactionAsync(tx);
                            return new UpdateDocumentResult { Success = false, IsServerError = true, Message = "Something went wrong while saving the replacement file." };
                        }
                    }

                    // Replacement was just physically written via _fileStorage above —
                    // record whichever backend is active now (may differ from
                    // whatever wrote the original, if the mode's been switched since).
                    await _dbService.ExecuteAsync<dynamic>(
                        "UPDATE bacpdfsign.dbo.document_attach SET doc_pages = @DocPages, doc_type = @DocType WHERE doc_id = @DocId",
                        new { DocPages = pageCount, DocType = MapStorageModeToDocType(_fileStorage.Mode), DocId = docId },
                        CommandType.Text,
                        tx);
                }

                await _dbService.CommitTransactionAsync(tx);

                return new UpdateDocumentResult
                {
                    Success = true,
                    DocId = docId,
                    DocName = existing.DocName,
                    DocDirectory = existing.DocDirectory,
                    DocPages = pageCount,
                    SignatoryCount = meta.Signatories.Count
                };
            }
            catch
            {
                try { await _dbService.RollbackTransactionAsync(tx); } catch { /* already rolled back or connection gone */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update document {DocId}", docId);
            return new UpdateDocumentResult { Success = false, IsServerError = true, Message = "Something went wrong" };
        }
    }

    // Admin-only override edit — see ISigningService for why this exists
    // separately from UpdateDocumentAsync (no signed-signatory lock, and it
    // can set doc_code/doc_status_id which UpdateDocumentAsync never touches).
    public async Task<AdminUpdateDocumentResult> AdminUpdateDocumentAsync(AdminUpdateDocumentRequest request, IFormFile? pdfFile)
    {
        try
        {
            var existing = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachDetails, dynamic>(
                @"SELECT doc_id AS DocId, doc_directory AS DocDirectory, doc_name AS DocName,
                         doc_code AS DocCode, doc_pages AS DocPages
                  FROM bacpdfsign.dbo.document_attach WHERE doc_id = @DocId",
                new { request.DocId },
                CommandType.Text);

            if (existing == null)
                return new AdminUpdateDocumentResult { Success = false, Message = "Document not found." };

            var newDocCode = string.IsNullOrWhiteSpace(request.DocCode) ? existing.DocCode : request.DocCode;
            if (!string.IsNullOrWhiteSpace(request.DocCode) && !string.Equals(request.DocCode, existing.DocCode, StringComparison.Ordinal))
            {
                var dupCount = await _dbService.ExecuteScalarAsync<int, dynamic>(
                    "SELECT COUNT(*) FROM bacpdfsign.dbo.document_attach WHERE doc_code = @DocCode AND doc_id != @DocId",
                    new { request.DocCode, request.DocId },
                    CommandType.Text);
                if (dupCount > 0)
                    return new AdminUpdateDocumentResult { Success = false, Message = "That document code is already in use." };
            }

            var tx = await _dbService.BeginTransactionAsync();
            try
            {
                await _dbService.ExecuteAsync<dynamic>(
                    @"UPDATE bacpdfsign.dbo.document_attach
                      SET doc_description = COALESCE(@Description, doc_description),
                          doc_code = @DocCode,
                          doc_status_id = COALESCE(@DocStatusId, doc_status_id),
                          doc_type_id = COALESCE(@DocTypeId, doc_type_id),
                          doc_datetime_update = CONVERT(NVARCHAR(50), GETDATE(), 100)
                      WHERE doc_id = @DocId",
                    new
                    {
                        request.Description,
                        DocCode = newDocCode,
                        request.DocStatusId,
                        request.DocTypeId,
                        request.DocId
                    },
                    CommandType.Text,
                    tx);

                var pageCount = existing.DocPages ?? 0;
                if (pdfFile != null)
                {
                    var targetDirFull = Path.Combine(NetworkPath, existing.DocDirectory ?? "");
                    using (_fileStorage.Connect(NetworkPath))
                    {
                        using var ms = new MemoryStream();
                        await pdfFile.CopyToAsync(ms);
                        var pdfBytes = ms.ToArray();

                        try
                        {
                            using var pdfdoc = new Spire.Pdf.PdfDocument();
                            pdfdoc.LoadFromBytes(pdfBytes);
                            pageCount = pdfdoc.Pages.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load admin-replacement PDF to count pages for doc {DocId}", request.DocId);
                            pageCount = 0;
                        }

                        var destFile = Path.Combine(targetDirFull, string.IsNullOrWhiteSpace(existing.DocName) ? "file.pdf" : existing.DocName);
                        var (saved, _) = await WriteFileWithRetryAsync(destFile, pdfBytes, targetDirFull);

                        if (!saved)
                        {
                            await _dbService.RollbackTransactionAsync(tx);
                            return new AdminUpdateDocumentResult { Success = false, IsServerError = true, Message = "Something went wrong while saving the replacement file." };
                        }
                    }

                    await _dbService.ExecuteAsync<dynamic>(
                        "UPDATE bacpdfsign.dbo.document_attach SET doc_pages = @DocPages, doc_type = @DocType WHERE doc_id = @DocId",
                        new { DocPages = pageCount, DocType = MapStorageModeToDocType(_fileStorage.Mode), DocId = request.DocId },
                        CommandType.Text,
                        tx);
                }

                await _dbService.CommitTransactionAsync(tx);
                return new AdminUpdateDocumentResult { Success = true, DocId = request.DocId, DocPages = pageCount };
            }
            catch
            {
                try { await _dbService.RollbackTransactionAsync(tx); } catch { /* already rolled back or connection gone */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed admin update for document {DocId}", request.DocId);
            return new AdminUpdateDocumentResult { Success = false, IsServerError = true, Message = "Something went wrong" };
        }
    }

    // Admin-only signatory edit — only ever deletes/reinserts sig_status = 0
    // (pending) rows so already-signed rows (and the sig_id history keyed
    // off them) are never touched. See ISigningService for the full rationale.
    public async Task<AdminUpdateSignatoriesResult> AdminUpdateSignatoriesAsync(AdminUpdateSignatoriesRequest request)
    {
        try
        {
            var doc = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachDetails, dynamic>(
                "SELECT doc_id AS DocId, doc_code AS DocCode FROM bacpdfsign.dbo.document_attach WHERE doc_id = @DocId",
                new { request.DocId },
                CommandType.Text);
            if (doc == null)
                return new AdminUpdateSignatoriesResult { Success = false, Message = "Document not found." };

            var maxSignedOrder = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"SELECT ISNULL(MAX(sig_order), 0) FROM bacpdfsign.dbo.document_signatories
                  WHERE doc_id = @DocId AND sig_status = 1",
                new { request.DocId },
                CommandType.Text);

            if (request.Signatories.Any(s => s.Order > 0 && s.Order <= maxSignedOrder))
            {
                return new AdminUpdateSignatoriesResult
                {
                    Success = false,
                    Message = $"Signatory order must be greater than {maxSignedOrder} — earlier steps are already signed."
                };
            }

            var tx = await _dbService.BeginTransactionAsync();
            try
            {
                await _dbService.ExecuteAsync<dynamic>(
                    "DELETE FROM bacpdfsign.dbo.document_signatories WHERE doc_id = @DocId AND sig_status = 0",
                    new { request.DocId },
                    CommandType.Text,
                    tx);

                var order = maxSignedOrder + 1;
                foreach (var sig in request.Signatories)
                {
                    await _dbService.ExecuteAsync<dynamic>(
                        @"INSERT INTO bacpdfsign.dbo.document_signatories
                            (doc_id, sig_code, sig_eid, sig_status, sig_order, sig_remarks,
                             sig_query_signed, sig_query_return, sig_user_type, sig_level,
                             sig_sign_count, sig_remarks_datenTime, date_time_inserted)
                          VALUES
                            (@DocId, @SigCode, @SigEid, 0, @SigOrder, '',
                             @QuerySigned, @QueryReturn, @SigUserType, @SigLevel,
                             @SigSignCount, '', CONVERT(NVARCHAR(50), GETDATE(), 100));",
                        new
                        {
                            request.DocId,
                            SigCode = doc.DocCode,
                            SigEid = sig.Eid,
                            SigOrder = sig.Order > 0 ? sig.Order : order,
                            QuerySigned = (object?)sig.QuerySigned ?? DBNull.Value,
                            QueryReturn = (object?)sig.QueryReturn ?? DBNull.Value,
                            SigUserType = sig.UserType,
                            SigLevel = NormalizeSigLevel(sig.Level),
                            SigSignCount = sig.NumSignatures > 0 ? sig.NumSignatures : 1
                        },
                        CommandType.Text,
                        tx);
                    order++;
                }

                await _dbService.CommitTransactionAsync(tx);
                return new AdminUpdateSignatoriesResult { Success = true, SignatoryCount = request.Signatories.Count };
            }
            catch
            {
                try { await _dbService.RollbackTransactionAsync(tx); } catch { /* already rolled back or connection gone */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed admin signatory update for document {DocId}", request.DocId);
            return new AdminUpdateSignatoriesResult { Success = false, IsServerError = true, Message = "Something went wrong" };
        }
    }

    public async Task<DeleteDocumentResult> DeleteDocumentAsync(int docId)
    {
        try
        {
            var existing = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachDetails, dynamic>(
                "SELECT doc_id AS DocId, doc_directory AS DocDirectory, doc_name AS DocName FROM bacpdfsign.dbo.document_attach WHERE doc_id = @DocId",
                new { DocId = docId },
                CommandType.Text);

            if (existing == null)
                return new DeleteDocumentResult { Success = false, Message = "Document not found." };

            var tx = await _dbService.BeginTransactionAsync();
            try
            {
                await _dbService.ExecuteAsync<dynamic>(
                    "DELETE FROM bacpdfsign.dbo.document_support WHERE doc_id = @DocId",
                    new { DocId = docId }, CommandType.Text, tx);

                await _dbService.ExecuteAsync<dynamic>(
                    "DELETE FROM bacpdfsign.dbo.document_signatories WHERE doc_id = @DocId",
                    new { DocId = docId }, CommandType.Text, tx);

                await _dbService.ExecuteAsync<dynamic>(
                    "DELETE FROM bacpdfsign.dbo.document_attach WHERE doc_id = @DocId",
                    new { DocId = docId }, CommandType.Text, tx);

                await _dbService.CommitTransactionAsync(tx);
            }
            catch
            {
                try { await _dbService.RollbackTransactionAsync(tx); } catch { /* already rolled back or connection gone */ }
                throw;
            }

            // DB rows are gone and durable at this point — anything below is
            // best-effort; a failure here must not read as "the delete failed".
            if (!string.IsNullOrWhiteSpace(existing.DocDirectory))
            {
                try
                {
                    var folderPath = Path.Combine(NetworkPath, existing.DocDirectory);
                    using (_fileStorage.Connect(NetworkPath))
                    {
                        await _fileStorage.DeleteDirectoryAsync(folderPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete physical folder for deleted doc_id {DocId}", docId);
                    return new DeleteDocumentResult { Success = true, PhysicalCleanupWarning = true };
                }
            }

            return new DeleteDocumentResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete document {DocId}", docId);
            return new DeleteDocumentResult { Success = false, IsServerError = true, Message = "Something went wrong" };
        }
    }

    // Shared by SaveDocumentAsync's supporting-file loop and AddSupportingFileAsync.
    private async Task<(bool saved, string? error)> WriteSupportingFileWithRetryAsync(string destFile, IFormFile file)
    {
        const int maxRetries = 3;
        const int delayMs = 2000;

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var fileBytes = ms.ToArray();

        if (_fileStorage.Mode == "S3")
        {
            await _fileStorage.WriteFileAsync(destFile, fileBytes);
            return (true, null);
        }

        var attempt = 0;
        while (attempt < maxRetries)
        {
            try
            {
                attempt++;
                await _fileStorage.WriteFileAsync(destFile, fileBytes);
                return (true, null);
            }
            catch (IOException ioEx) when (attempt < maxRetries)
            {
                _logger.LogWarning(ioEx, "Retry {Attempt}/{Max} saving supporting file to {Dest}", attempt, maxRetries, destFile);
                await Task.Delay(delayMs);
            }
        }
        return (false, "Something went wrong while saving the supporting file.");
    }

    public async Task<AddSupportingFileResult> AddSupportingFileAsync(int docId, int eid, IFormFile file)
    {
        try
        {
            var existing = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachDetails, dynamic>(
                "SELECT doc_id AS DocId, doc_directory AS DocDirectory, doc_code AS DocCode FROM bacpdfsign.dbo.document_attach WHERE doc_id = @DocId",
                new { DocId = docId }, CommandType.Text);

            if (existing == null)
                return new AddSupportingFileResult { Success = false, Message = "Document not found." };

            var supportDirFull = Path.Combine(NetworkPath, existing.DocDirectory ?? "", "support");
            var supportDirRelative = Path.Combine(existing.DocDirectory ?? "", "support");
            var sanitizedName = Path.GetFileName(file.FileName);

            using (_fileStorage.Connect(NetworkPath))
            {
                var destFile = Path.Combine(supportDirFull, sanitizedName);
                var (saved, error) = await WriteSupportingFileWithRetryAsync(destFile, file);
                if (!saved)
                    return new AddSupportingFileResult { Success = false, IsServerError = true, Message = error };
            }

            var location = Path.Combine(supportDirRelative, sanitizedName);
            var supId = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"INSERT INTO bacpdfsign.dbo.document_support
                    (sup_doc_name, sup_code, sup_document, sup_type, sup_datetme, sup_eid, doc_id, sup_location)
                  OUTPUT INSERTED.sup_id
                  VALUES
                    (@Name, @Code, NULL, @Type, CONVERT(NVARCHAR(50), GETDATE(), 100), @Eid, @DocId, @Location);",
                new
                {
                    Name = sanitizedName,
                    Code = existing.DocCode,
                    Type = file.ContentType,
                    Eid = eid,
                    DocId = docId,
                    Location = location
                },
                CommandType.Text);

            return new AddSupportingFileResult { Success = true, SupId = supId, SupDocName = sanitizedName, SupLocation = location };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add supporting file for doc {DocId}", docId);
            return new AddSupportingFileResult { Success = false, IsServerError = true, Message = "Something went wrong" };
        }
    }

    public async Task<DeleteSupportingFileResult> DeleteSupportingFileAsync(int supId)
    {
        try
        {
            var existing = await _dbService.QueryFirstOrDefaultAsync<DocumentSupportViewDto, dynamic>(
                "SELECT sup_id AS SupId, doc_id AS DocId, sup_location AS SupLocation FROM bacpdfsign.dbo.document_support WHERE sup_id = @SupId",
                new { SupId = supId }, CommandType.Text);

            if (existing == null)
                return new DeleteSupportingFileResult { Success = false, Message = "Supporting document not found." };

            await _dbService.ExecuteAsync<dynamic>(
                "DELETE FROM bacpdfsign.dbo.document_support WHERE sup_id = @SupId",
                new { SupId = supId }, CommandType.Text);

            if (!string.IsNullOrWhiteSpace(existing.SupLocation))
            {
                try
                {
                    using (_fileStorage.Connect(NetworkPath))
                    {
                        var filePath = Path.Combine(NetworkPath, existing.SupLocation);
                        await _fileStorage.DeleteFileAsync(filePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete physical file for deleted supporting doc {SupId}", supId);
                }
            }

            return new DeleteSupportingFileResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete supporting file {SupId}", supId);
            return new DeleteSupportingFileResult { Success = false, IsServerError = true, Message = "Something went wrong" };
        }
    }

    private async Task LogError(string eid, string userType, string docId, string message)
    {
        try
        {
            await _dbService.ExecuteAsync<dynamic>(
                @"INSERT INTO bacpdfsign.dbo.error_log (eid, user_type, doc_id, error_message, error_datetime)
                  VALUES (@Eid, @UserType, @DocId, @Message, GETDATE())",
                new { Eid = eid, UserType = userType, DocId = docId, Message = message },
                CommandType.Text);
        }
        catch
        {
            _logger.LogError("Failed to log error to DB: {Message}", message);
        }
    }

    // The signing save (SaveSignatureImageAsync) already awaits its own write
    // + retry-with-backoff before returning success, so by the time the
    // client's follow-up viewer request lands here the file should exist —
    // but NAS/SMB shares can still lag a read behind a very recent write on
    // a fresh connection (metadata caching), which showed up as "press Retry
    // to see the signed PDF" right after signing. A few short retries here
    // absorbs that lag server-side instead of pushing it onto the user.
    private async Task<byte[]?> ReadFileWithRetryAsync(string path, int maxAttempts = 3, int delayMs = 400)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var bytes = await _fileStorage.ReadFileAsync(path);
            if (bytes != null) return bytes;
            if (attempt < maxAttempts) await Task.Delay(delayMs);
        }
        return null;
    }

    private async Task<string> FindSignedPdfPathAsync(string docId)
    {
        var currentYearShort = DateTime.Now.Year % 100;
        for (int y = currentYearShort; y >= currentYearShort - 5; y--)
        {
            var path = Path.Combine(DigitalSignaturePath, y.ToString(), $"Form{docId}", "file.pdf");
            if (await _fileStorage.FileExistsAsync(path)) return path;
        }
        // Legacy fallback: path without year folder
        return Path.Combine(DigitalSignaturePath, $"Form{docId}", "file.pdf");
    }

    /// <summary>
    /// Reads a signed PDF directly off the NAS share, independent of the
    /// currently configured FileStorage mode. Used for legacy documents
    /// (doc_type 0/1) whose files live only on the NAS and were never
    /// migrated into the app's current storage backend (S3/Local/Custom).
    /// Mirrors FindSignedPdfPathAsync's search order (current year down to
    /// 5 years back, then the pre-year-folder legacy layout).
    /// </summary>
    private async Task<byte[]?> ReadSignedPdfFromNasAsync(string docId, NetworkCredential credentials)
    {
        var nasShareRoot = _credentialService.GetNetworkSharePath();
        var digitalSignatureRoot = Path.Combine(nasShareRoot, "digital_signature");

        using (new ConnectToSharedFolder(nasShareRoot, credentials))
        {
            var currentYearShort = DateTime.Now.Year % 100;
            for (int y = currentYearShort; y >= currentYearShort - 5; y--)
            {
                var path = Path.Combine(digitalSignatureRoot, y.ToString(), $"Form{docId}", "file.pdf");
                if (System.IO.File.Exists(path)) return await System.IO.File.ReadAllBytesAsync(path);
            }

            // Legacy fallback: path without year folder
            var legacyPath = Path.Combine(digitalSignatureRoot, $"Form{docId}", "file.pdf");
            return System.IO.File.Exists(legacyPath) ? await System.IO.File.ReadAllBytesAsync(legacyPath) : null;
        }
    }

    /// <summary>
    /// Loads the PDF bytes for a legacy doc_type == 0 row directly from
    /// document_attach.doc_attachement — varbinary first, base64-text
    /// fallback. Shared by GetOriginalPdfBytes and the lazy binary→file
    /// migration in MigrateLegacyBinaryIfNeededAsync.
    /// </summary>
    private async Task<byte[]?> LoadLegacyBlobBytesAsync(DocumentAttachDetails docDetails)
    {
        // Blob columns are only selected by callers when cheap to do so —
        // load them here on demand if the caller didn't already have them.
        if ((docDetails.DocAttachment == null || docDetails.DocAttachment.Length == 0)
            && string.IsNullOrEmpty(docDetails.DocAttachmentText))
        {
            var blob = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachmentBlob, dynamic>(
                @"SELECT doc_attachement AS DocAttachment,
                  CAST(doc_attachement AS NVARCHAR(MAX)) AS DocAttachmentText
                  FROM [bacpdfsign].[dbo].[document_attach] WHERE doc_id = @DocId",
                new { docDetails.DocId },
                CommandType.Text);
            docDetails.DocAttachment = blob?.DocAttachment;
            docDetails.DocAttachmentText = blob?.DocAttachmentText;
        }

        // Binary blob (varbinary column)
        if (docDetails.DocAttachment != null && docDetails.DocAttachment.Length > 0)
            return docDetails.DocAttachment;

        // Fallback: varchar column storing base64-encoded PDF
        if (!string.IsNullOrEmpty(docDetails.DocAttachmentText))
        {
            try
            {
                var clean = Regex.Replace(docDetails.DocAttachmentText, @"^data:[^;]+;base64,", string.Empty);
                var bytes = Convert.FromBase64String(clean);
                if (bytes.Length > 0) return bytes;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Base64 decode failed for doc_id: {DocId}", docDetails.DocId);
            }
        }

        _logger.LogWarning("No blob attachment found for doc_id: {DocId}", docDetails.DocId);
        return null;
    }

    /// <summary>
    /// Gets the original PDF bytes based on doc_type.
    /// doc_type 0 = blob from DB, doc_type 1 = file from network share.
    /// </summary>
    private async Task<byte[]?> GetOriginalPdfBytes(
        DocumentAttachDetails docDetails,
        NetworkCredential credentials,
        string networkPath)
    {
        if (docDetails.DocType == 0)
        {
            return await LoadLegacyBlobBytesAsync(docDetails);
        }
        else
        {
            // File-based document: read from network share
            if (string.IsNullOrEmpty(docDetails.DocDirectory) || string.IsNullOrEmpty(docDetails.DocName))
            {
                _logger.LogWarning("No directory/name found for doc_id: {DocId}", docDetails.DocId);
                return null;
            }

            // Legacy NAS-stored document (doc_type == 1), but the currently
            // configured storage backend is something else (S3 in production
            // today) — _fileStorage would look for it as an S3 key and never
            // find it. Read straight off the NAS share instead, same fallback
            // ReadSignedPdfFromNasAsync already uses for already-signed
            // doc_type 0/1 documents above.
            if (docDetails.DocType == 1 && _fileStorage.Mode != "Nas")
            {
                var nasShareRoot = _credentialService.GetNetworkSharePath();
                using (new ConnectToSharedFolder(nasShareRoot, credentials))
                {
                    var nasFilePath = Path.Combine(nasShareRoot, docDetails.DocDirectory, docDetails.DocName);
                    var nasBytes = File.Exists(nasFilePath) ? await File.ReadAllBytesAsync(nasFilePath) : null;
                    if (nasBytes == null)
                        _logger.LogWarning("PDF file not found on NAS at: {Path} for doc_id: {DocId}", nasFilePath, docDetails.DocId);
                    return nasBytes;
                }
            }

            using (_fileStorage.Connect(networkPath))
            {
                var filePath = Path.Combine(networkPath, docDetails.DocDirectory, docDetails.DocName);

                var bytes = await _fileStorage.ReadFileAsync(filePath);
                if (bytes == null)
                {
                    _logger.LogWarning("PDF file not found at: {Path} for doc_id: {DocId}", filePath, docDetails.DocId);
                    return null;
                }

                return bytes;
            }
        }
    }

    /// <summary>
    /// Applies a diagonal watermark to the PDF and returns the watermarked bytes.
    /// </summary>
    private byte[] ApplyWatermark(byte[] pdfBytes, string watermarkText)
    {
        using var pdfdoc = new Spire.Pdf.PdfDocument();
        pdfdoc.LoadFromBytes(pdfBytes);

        var font = new Spire.Pdf.Graphics.PdfFont(Spire.Pdf.Graphics.PdfFontFamily.Helvetica, 40f);
        var brush = new Spire.Pdf.Graphics.PdfSolidBrush(
            new Spire.Pdf.Graphics.PdfRGBColor(System.Drawing.Color.FromArgb(30, 255, 0, 0)));

        for (int i = 0; i < pdfdoc.Pages.Count; i++)
        {
            var page = pdfdoc.Pages[i];
            var pageSize = page.Size;

            page.Canvas.Save();
            page.Canvas.TranslateTransform(pageSize.Width / 2, pageSize.Height / 2);
            page.Canvas.RotateTransform(-45);

            var textSize = font.MeasureString(watermarkText);
            page.Canvas.DrawString(
                watermarkText,
                font,
                brush,
                -textSize.Width / 2,
                -textSize.Height / 2);

            page.Canvas.Restore();
        }

        using var stream = new MemoryStream();
        pdfdoc.SaveToStream(stream);
        pdfdoc.Close();

        return stream.ToArray();
    }
}
