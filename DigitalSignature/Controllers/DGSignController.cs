using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using System.Data;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using DigitalSignature.Services;
using DigitalSignature.Models;
using System.Text.RegularExpressions;
using SPMS;


namespace DigitalSignature.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class DGSignController : Controller
    {
        private readonly IDatabaseService _dbService;
        private readonly ISecureCredentialService _credentialService;
        private readonly IFileStorageService _fileStorage;
        private readonly ILogger<DGSignController> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly ISigningService _signingService;
        private readonly IAesTokenService _aesTokenService;
        private readonly IConfiguration _configuration;

        // Base paths now resolve through _fileStorage so they follow whichever
        // location (NAS / Local / Custom) is active in the FileStorage config.
        private string NetworkPath => _fileStorage.RootPath;
        private string DigitalSignaturePath => _fileStorage.DigitalSignaturePath;
        private string DigitalSpicimenPath => _fileStorage.DigitalSpicimenPath;

        public DGSignController(
            IDatabaseService dbService,
            ISecureCredentialService credentialService,
            IFileStorageService fileStorage,
            ILogger<DGSignController> logger,
            IWebHostEnvironment env,
            ISigningService signingService,
            IAesTokenService aesTokenService,
            IConfiguration configuration)
        {
            _dbService = dbService;
            _credentialService = credentialService;
            _fileStorage = fileStorage;
            _logger = logger;
            _env = env;
            _signingService = signingService;
            _aesTokenService = aesTokenService;
            _configuration = configuration;
        }

        // ==================== GET PNPKI CERTIFICATE EXPIRATION ====================

        [HttpGet("get_pnpki_expiration")]
        public async Task<IActionResult> GetPnpkiExpiration()
        {
            try
            {
                // 1. Get all active users with PFX certificates
                var sql = @"
                    SELECT a.id AS PfxId, a.eid AS Eid, a.user_type AS UserType,
                           a.pfx_attachement AS PfxAttachment, a.passwords AS Password,
                           a.code AS Code, b.fname AS Fname,
                           b.position AS Position, b.office_name AS OfficeName
                    FROM bacpdfsign.dbo.pfx_attachments AS a
                    LEFT JOIN [bacpdfsign].[dbo].[signatory_names] AS b 
                        ON a.eid = b.eid AND a.user_type = b.user_type
                    WHERE a.user_type = 0 AND a.reg_id = 0 AND a.active = 1";

                var users = (await _dbService.QueryAsync<PnpkiUserDto, dynamic>(
                    sql, new { }, CommandType.Text)).ToList();

                if (users.Count == 0)
                    return Ok(Array.Empty<PnpkiExpirationDto>());

                // 2. For each user, load the PFX certificate and read expiration
                var results = new List<PnpkiExpirationDto>();

                foreach (var user in users)
                {
                    var dto = new PnpkiExpirationDto
                    {
                        PfxId = user.PfxId,
                        Eid = user.Eid,
                        UserType = user.UserType,
                        Fname = user.Fname,
                        Position = user.Position,
                        OfficeName = user.OfficeName
                    };

                    if (user.PfxAttachment == null || user.PfxAttachment.Length == 0)
                    {
                        dto.Status = "No Certificate";
                        results.Add(dto);
                        continue;
                    }

                    try
                    {
                        // Try loading the certificate with the stored password first
                        X509Certificate2? cert = null;
                        var password = string.IsNullOrEmpty(user.Password)
                            ? string.Empty
                            : SPMS.Rijndael.Decrypt(user.Password) ?? string.Empty;
                        try
                        {
                            cert = new X509Certificate2(user.PfxAttachment, password,
                                X509KeyStorageFlags.EphemeralKeySet);
                        }
                        catch
                        {
                            // Password from DB may be encrypted/incorrect for direct PFX open.
                            // Fall back: extract the first certificate from the PFX without the private key.
                            cert = ExtractCertificateFromPfx(user.PfxAttachment);
                        }

                        if (cert == null)
                        {
                            dto.Status = "Unreadable Certificate";
                            results.Add(dto);
                            continue;
                        }

                        using (cert)
                        {
                            dto.CertificateSubject = cert.Subject;
                            dto.CertificateIssuer = cert.Issuer;
                            dto.NotBefore = cert.NotBefore;
                            dto.NotAfter = cert.NotAfter;
                            dto.SerialNumber = cert.SerialNumber;
                            dto.Thumbprint = cert.Thumbprint;

                            var now = DateTime.Now;
                            if (now > cert.NotAfter)
                                dto.Status = "Expired";
                            else if (now > cert.NotAfter.AddDays(-30))
                                dto.Status = "Expiring Soon";
                            else
                                dto.Status = "Valid";

                            dto.DaysUntilExpiration = (int)(cert.NotAfter - now).TotalDays;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read certificate for eid: {Eid}", user.Eid);
                        dto.Status = "Error";
                        dto.ErrorMessage = ex.Message;
                    }

                    results.Add(dto);
                }

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting PNPKI certificate expirations");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching certificate expirations", error = ex.Message });
            }
        }

        [HttpGet("get_pnpki_expiration_by_eid")]
        public async Task<IActionResult> GetPnpkiExpirationByEid(
            [FromQuery] int eid,
            [FromQuery] int userType)
        {
            try
            {
                var sql = @"
                    SELECT a.id AS PfxId, a.eid AS Eid, a.user_type AS UserType,
                           a.pfx_attachement AS PfxAttachment, a.passwords AS Password,
                           a.code AS Code, b.fname AS Fname,
                           b.position AS Position, b.office_name AS OfficeName
                    FROM bacpdfsign.dbo.pfx_attachments AS a
                    LEFT JOIN [bacpdfsign].[dbo].[signatory_names] AS b 
                        ON a.eid = b.eid AND a.user_type = b.user_type
                    WHERE a.eid = @Eid AND a.user_type = @UserType AND a.active = 1";

                var user = await _dbService.QueryFirstOrDefaultAsync<PnpkiUserDto, dynamic>(
                    sql, new { Eid = eid, UserType = userType }, CommandType.Text);

                if (user == null)
                    return NotFound(new { success = false, message = "No active certificate found for this user" });

                var dto = new PnpkiExpirationDto
                {
                    PfxId = user.PfxId,
                    Eid = user.Eid,
                    UserType = user.UserType,
                    Fname = user.Fname,
                    Position = user.Position,
                    OfficeName = user.OfficeName
                };

                if (user.PfxAttachment == null || user.PfxAttachment.Length == 0)
                {
                    dto.Status = "No Certificate";
                    return Ok(dto);
                }

                X509Certificate2? cert = null;
                var password = string.IsNullOrEmpty(user.Password)
                    ? string.Empty
                    : Rijndael.Decrypt(user.Password) ?? string.Empty;
                try
                {
                    cert = new X509Certificate2(user.PfxAttachment, password,
                        X509KeyStorageFlags.EphemeralKeySet);
                }
                catch
                {
                    cert = ExtractCertificateFromPfx(user.PfxAttachment);
                }

                if (cert == null)
                {
                    dto.Status = "Unreadable Certificate";
                    return Ok(dto);
                }

                using (cert)
                {
                    dto.CertificateSubject = cert.Subject;
                    dto.CertificateIssuer = cert.Issuer;
                    dto.NotBefore = cert.NotBefore;
                    dto.NotAfter = cert.NotAfter;
                    dto.SerialNumber = cert.SerialNumber;
                    dto.Thumbprint = cert.Thumbprint;

                    var now = DateTime.Now;
                    if (now > cert.NotAfter)
                        dto.Status = "Expired";
                    else if (now > cert.NotAfter.AddDays(-30))
                        dto.Status = "Expiring Soon";
                    else
                        dto.Status = "Valid";

                    dto.DaysUntilExpiration = (int)(cert.NotAfter - now).TotalDays;
                }

                return Ok(dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting PNPKI certificate expiration for eid: {Eid}", eid);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching certificate expiration", error = ex.Message });
            }
        }

        [HttpGet("get_pending_documents")]
        public async Task<IActionResult> GetPendingDocuments(
    [FromQuery] int year,
    [FromQuery] int eid,
    [FromQuery] int userType,
    [FromQuery] int? docTypeId = null)
        {
            try
            {
                var results = await _signingService.GetPendingDocumentsAsync(year, eid, userType, docTypeId);
                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting pending documents for eid: {Eid}, year: {Year}", eid, year);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching pending documents", error = ex.Message });
            }
        }

        [HttpGet("get_pending_document_types")]
        public async Task<IActionResult> GetPendingDocumentTypes(
            [FromQuery] int year,
            [FromQuery] int eid,
            [FromQuery] int userType)
        {
            try
            {
                var results = await _signingService.GetPendingDocumentTypesAsync(year, eid, userType);
                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting pending document types for eid: {Eid}, year: {Year}", eid, year);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching pending document types", error = ex.Message });
            }
        }

        [HttpGet("get_signatory_names")]
        public async Task<IActionResult> GetSignatoryNames()
        {
            try
            {
                var sql = @"
                    SELECT [eid] AS Eid,
                           [fname] AS Fname,
                           [position] AS Position,
                           [offices] AS Offices,
                           [office_name] AS OfficeName,
                           [email] AS Email,
                           [user_type] AS UserType,
                           [cp] AS Cp,
                           [active] AS Active,
                           [multi_id] AS MultiId,
                           [f_email_address] AS FEmailAddress
                    FROM [bacpdfsign].[dbo].[signatory_names]";

                var results = (await _dbService.QueryAsync<SignatoryListDto, dynamic>(
                    sql,
                    new { },
                    CommandType.Text)).ToList();

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting signatory names");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching signatory names", error = ex.Message });
            }
        }

        [HttpGet("get_offices")]
        public async Task<IActionResult> GetOffices()
        {
            try
            {
                var sql = @"
                    SELECT DISTINCT
                           [offices] AS Id,
                           [office_name] AS Value
                    FROM [bacpdfsign].[dbo].[signatory_names]
                    WHERE [offices] IS NOT NULL AND [offices] != 0
                    ORDER BY [office_name]";

                var results = (await _dbService.QueryAsync<DynamicSelectDto, dynamic>(
                    sql,
                    new { },
                    CommandType.Text)).ToList();

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting offices");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching offices", error = ex.Message });
            }
        }

        [HttpGet("get_signatories")]
        public async Task<IActionResult> GetSignatories([FromQuery] string? officeId)
        {
            try
            {
                var sql = @"
                    SELECT [eid] AS Eid,
                           [fname] AS Fname,
                           [position] AS Position,
                           [offices] AS Offices,
                           [office_name] AS OfficeName,
                           [email] AS Email,
                           [user_type] AS UserType,
                           [cp] AS Cp,
                           [active] AS Active,
                           [multi_id] AS MultiId,
                           [f_email_address] AS FEmailAddress
                    FROM [bacpdfsign].[dbo].[signatory_names]"
                           + (string.IsNullOrEmpty(officeId) ? string.Empty : " WHERE [offices] = @OfficeId");

                var results = (await _dbService.QueryAsync<SignatoryListDto, dynamic>(
                    sql,
                    new { OfficeId = officeId },
                    CommandType.Text)).ToList();

                var signatoryList = results.Select(r => new DynamicSelectDto
                {
                    Id = $"{r.Eid}:{r.UserType}",
                    Value = string.IsNullOrWhiteSpace(r.Position)
                        ? r.Fname ?? string.Empty
                        : $"{r.Fname} - {r.Position}",
                    Abbr_Value = r.Position,
                    Additional_Id = r.UserType.ToString()
                }).ToList();

                return Ok(signatoryList);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting signatories");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching signatories", error = ex.Message });
            }
        }

        [HttpGet("documet_uploaded")]
        public async Task<IActionResult> GetDocumentUploaded(
            [FromQuery] int doc_eid,
            [FromQuery] int doc_eid_user_type,
            [FromQuery] int year)
        {
            try
            {
                var sql = @" SELECT a.[doc_id]            AS DocId,
                            a.[doc_name]          AS DocName,
                            a.[doc_type]          AS DocType,
                            a.[doc_description]   AS DocDescription,
                            a.[doc_status_id]     AS DocStatusId,
                            a.[doc_designated]    AS DocDesignated,
                            a.[doc_datetime]      AS DocDatetime,
                            a.[doc_eid]           AS DocEid,
                            a.[doc_datetime_update] AS DocDatetimeUpdate,
                            a.[doc_code]          AS DocCode,
                            a.[doc_signatory_type] AS DocSignatoryType,
                            a.[doc_is]            AS DocIs,
                            a.[doc_type_id]       AS DocTypeId,
                            a.[doc_eid_user_type] AS DocEidUserType,
                            a.[doc_pages]         AS DocPages,
                            a.[doc_transaction]   AS DocTransaction,
                            b.[document_description] AS DocumentDescription,
                            b.[document_abbr]     AS DocumentAbbr,
                            c.[status_type]       AS StatusType
                        FROM [bacpdfsign].[dbo].[document_attach] as a
                        left join [bacpdfsign].[dbo].[document_types] as b
                            on a.doc_type_id = b.id
                        left join [bacpdfsign].[dbo].[req_status] as c
                            on a.doc_status_id = c.id
                        WHERE a.doc_eid = @DocEid
                        AND a.doc_eid_user_type = @DocEidUserType
                        AND YEAR(a.doc_datetime) = @Year";

                var results = await _dbService.QueryAsync<DocumentUploadedDto, dynamic>(
                    sql,
                    new { DocEid = doc_eid, DocEidUserType = doc_eid_user_type, Year = year },
                    CommandType.Text);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting uploaded documents for eid: {doc_eid} and year: {year}");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching uploaded documents", error = ex.Message });
            }
        }

        // ==================== UPLOAD DOCUMENT ====================
        // Staff (JWT) entry point. Mirrored, for machine callers with no
        // staff session, by DocumentIntakeController (X-Service-Key auth) —
        // both delegate to the same ISigningService.SaveDocumentAsync.
        [HttpPost("upload_document")]
        [RequestSizeLimit(100_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
        public async Task<IActionResult> UploadDocument(
            [FromForm] string meta,
            IFormFile? pdfFile,
            List<IFormFile>? supportingFiles)
        {
            UploadDocumentMetaDto? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<UploadDocumentMetaDto>(
                    meta, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new { success = false, message = "meta is not valid JSON." });
            }
            if (parsed == null)
                return BadRequest(new { success = false, message = "meta is required." });

            var result = await _signingService.SaveDocumentAsync(parsed, pdfFile, supportingFiles);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new
            {
                success = true,
                docId = result.DocId,
                docCode = result.DocCode,
                docName = result.DocName,
                docDirectory = result.DocDirectory,
                docPages = result.DocPages,
                signatoryCount = result.SignatoryCount,
                supportingFileCount = result.SupportingFileCount
            });
        }

        // ==================== GET DOCUMENT FOR EDIT ====================
        [HttpGet("get_document_for_edit")]
        public async Task<IActionResult> GetDocumentForEdit([FromQuery] int docId)
        {
            try
            {
                var result = await _signingService.GetDocumentForEditAsync(docId);
                if (result?.Document == null)
                    return NotFound(new { success = false, message = "Document not found" });

                return Ok(new
                {
                    document = result.Document,
                    signatories = result.Signatories,
                    supportingDocuments = result.SupportingDocuments,
                    canEditMain = result.CanEditMain
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting document for edit, docId: {DocId}", docId);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching the document", error = ex.Message });
            }
        }

        // ==================== UPDATE DOCUMENT ====================
        [HttpPost("update_document")]
        [RequestSizeLimit(100_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
        public async Task<IActionResult> UpdateDocument(
            [FromQuery] int docId,
            [FromForm] string meta,
            IFormFile? pdfFile)
        {
            UploadDocumentMetaDto? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<UploadDocumentMetaDto>(
                    meta, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new { success = false, message = "meta is not valid JSON." });
            }
            if (parsed == null)
                return BadRequest(new { success = false, message = "meta is required." });

            var result = await _signingService.UpdateDocumentAsync(docId, parsed, pdfFile);
            if (!result.Success)
            {
                if (result.IsServerError)
                    return StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message });
                if (result.IsLocked)
                    return Conflict(new { success = false, message = result.Message });
                return BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new
            {
                success = true,
                docId = result.DocId,
                docName = result.DocName,
                docDirectory = result.DocDirectory,
                docPages = result.DocPages,
                signatoryCount = result.SignatoryCount
            });
        }

        // ==================== DELETE DOCUMENT ====================
        [HttpDelete("delete_document")]
        public async Task<IActionResult> DeleteDocument([FromQuery] int docId)
        {
            var result = await _signingService.DeleteDocumentAsync(docId);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : NotFound(new { success = false, message = result.Message });
            }
            return Ok(new { success = true, physicalCleanupWarning = result.PhysicalCleanupWarning });
        }

        // ==================== ADMIN: DOCUMENT SEARCH / OVERRIDE EDIT ====================
        // Free-text search over document_attach, plus the same office/type/
        // status/date filters as the "My Documents" page (get_signed_history)
        // so an admin can narrow down a document the same way a normal user
        // locates their own — no equivalent existed before (every other list
        // endpoint is scoped by eid, not free filters). Admin-only since it
        // can surface any document system-wide.
        [HttpGet("admin_search_documents")]
        public async Task<IActionResult> AdminSearchDocuments(
            [FromQuery] string field = "all",
            [FromQuery] string query = "",
            [FromQuery] string? officeId = null,
            [FromQuery] int? docTypeId = null,
            [FromQuery] int? statusId = null,
            [FromQuery] int? year = null,
            [FromQuery] string? startDate = null,
            [FromQuery] string? endDate = null,
            [FromQuery] int? employeeEid = null,
            [FromQuery] string? employeeRole = null)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var normalizedField = field?.ToLowerInvariant() switch
            {
                "doc_id" => "doc_id",
                "description" => "description",
                "doc_code" => "doc_code",
                _ => "all"
            };

            var normalizedEmployeeRole = employeeRole?.ToLowerInvariant() == "signed" ? "signed" : "uploaded";

            var sql = @"
                SELECT TOP 200
                    a.doc_id AS DocId, a.doc_name AS DocName, a.doc_description AS DocDescription,
                    a.doc_code AS DocCode, a.doc_status_id AS DocStatusId, a.doc_type_id AS DocTypeId,
                    a.doc_eid AS DocEid, a.doc_eid_user_type AS DocEidUserType,
                    a.doc_datetime AS DocDatetime, a.doc_datetime_update AS DocDatetimeUpdate,
                    b.document_description AS DocumentTypeName,
                    c.status_type AS StatusType,
                    sn.fname AS OwnerName, sn.office_name AS OwnerOffice
                FROM bacpdfsign.dbo.document_attach a
                LEFT JOIN bacpdfsign.dbo.document_types b ON a.doc_type_id = b.id
                LEFT JOIN bacpdfsign.dbo.req_status c ON a.doc_status_id = c.id
                LEFT JOIN bacpdfsign.dbo.signatory_names sn ON sn.eid = a.doc_eid AND sn.user_type = a.doc_eid_user_type
                WHERE (
                    (@Field = 'doc_id' AND CAST(a.doc_id AS NVARCHAR(20)) LIKE '%' + @Query + '%')
                    OR (@Field = 'description' AND a.doc_description LIKE '%' + @Query + '%')
                    OR (@Field = 'doc_code' AND a.doc_code LIKE '%' + @Query + '%')
                    OR (@Field = 'all')
                )
                AND (@OfficeId IS NULL OR sn.offices = @OfficeId)
                AND (@DocTypeId IS NULL OR a.doc_type_id = @DocTypeId)
                AND (@StatusId IS NULL OR a.doc_status_id = @StatusId)
                AND (
                    @EmployeeEid IS NULL
                    OR (@EmployeeRole = 'uploaded' AND a.doc_eid = @EmployeeEid)
                    OR (@EmployeeRole = 'signed' AND EXISTS (
                        SELECT 1 FROM bacpdfsign.dbo.document_signatories ds3
                        WHERE ds3.doc_id = a.doc_id AND ds3.sig_eid = @EmployeeEid
                    ))
                )
                AND (@Year IS NULL OR a.doc_datetime LIKE '%' + CAST(@Year AS NVARCHAR(20)) + '%')
                AND (
                    @Year IS NOT NULL OR @StartDate IS NULL OR @EndDate IS NULL
                    OR (
                        CASE WHEN ISDATE(a.doc_datetime) = 1 THEN CAST(CONVERT(datetime, a.doc_datetime, 100) AS DATE) END
                        BETWEEN CAST(@StartDate AS DATE) AND CAST(@EndDate AS DATE)
                    )
                )
                ORDER BY a.doc_datetime DESC";

            var rows = await _dbService.QueryAsync<AdminDocumentSearchResultDto, dynamic>(
                sql,
                new
                {
                    Field = normalizedField,
                    Query = query ?? "",
                    OfficeId = string.IsNullOrEmpty(officeId) ? null : officeId,
                    DocTypeId = docTypeId,
                    StatusId = statusId,
                    Year = year,
                    StartDate = startDate,
                    EndDate = endDate,
                    EmployeeEid = employeeEid,
                    EmployeeRole = normalizedEmployeeRole
                },
                CommandType.Text);
            return Ok(rows);
        }

        // Admin-only override edit of document_attach — no signed-signatory
        // lock (unlike update_document), and can set doc_code/doc_status_id
        // which update_document never touches. Never touches signatories.
        [HttpPost("admin_update_document")]
        [RequestSizeLimit(100_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
        public async Task<IActionResult> AdminUpdateDocument([FromForm] string meta, IFormFile? pdfFile)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            AdminUpdateDocumentRequest? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<AdminUpdateDocumentRequest>(
                    meta, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new { success = false, message = "meta is not valid JSON." });
            }
            if (parsed == null)
                return BadRequest(new { success = false, message = "meta is required." });

            var result = await _signingService.AdminUpdateDocumentAsync(parsed, pdfFile);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new { success = true, docId = result.DocId, docPages = result.DocPages });
        }

        // Admin-only signatory edit — only ever touches pending (sig_status =
        // 0) rows; already-signed rows are always preserved untouched.
        [HttpPost("admin_update_signatories")]
        public async Task<IActionResult> AdminUpdateSignatories([FromBody] AdminUpdateSignatoriesRequest request)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var result = await _signingService.AdminUpdateSignatoriesAsync(request);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new { success = true, signatoryCount = result.SignatoryCount });
        }

        // Admin override of a single signatory row's status — works on
        // already-signed rows too, unlike admin_update_signatories above.
        [HttpPost("admin_update_signatory_status")]
        public async Task<IActionResult> AdminUpdateSignatoryStatus([FromBody] AdminUpdateSignatoryStatusRequest request)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var result = await _signingService.AdminUpdateSignatoryStatusAsync(request);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new { success = true });
        }

        // ==================== ADD SUPPORTING DOCUMENT ====================
        [HttpPost("add_supporting_document")]
        [RequestSizeLimit(50_000_000)]
        public async Task<IActionResult> AddSupportingDocument(
            [FromQuery] int docId,
            [FromQuery] int eid,
            IFormFile file)
        {
            var result = await _signingService.AddSupportingFileAsync(docId, eid, file);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : BadRequest(new { success = false, message = result.Message });
            }
            return Ok(new { success = true, supId = result.SupId, supDocName = result.SupDocName, supLocation = result.SupLocation });
        }

        // ==================== DELETE SUPPORTING DOCUMENT ====================
        [HttpDelete("delete_supporting_document")]
        public async Task<IActionResult> DeleteSupportingDocument([FromQuery] int supId)
        {
            var result = await _signingService.DeleteSupportingFileAsync(supId);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : NotFound(new { success = false, message = result.Message });
            }
            return Ok(new { success = true });
        }

        [HttpGet("get_signed_history")]
        public async Task<IActionResult> GetSignedHistory(
            [FromQuery] int eid,
            [FromQuery] int userType,
            [FromQuery] int statusType,
            [FromQuery] string? officeId = null,
            [FromQuery] int? year = null,
            [FromQuery] string? startDate = "",
            [FromQuery] string? endDate = "",
            [FromQuery] int? docTypeId = null)
        {
            if (year == null && (string.IsNullOrEmpty(startDate) || string.IsNullOrEmpty(endDate)))
                return BadRequest(new { message = "Provide either 'year' or both 'startDate' and 'endDate'." });

            if (statusType < 0 || statusType > 3)
                return BadRequest(new { message = "statusType must be 0 (unsigned), 1 (signed), 2 (returned/cancelled), or 3 (uploaded)." });

            try
            {
                // statusType 0/1/2 are read straight off document_signatories.sig_status for
                // this signatory: 1=signed, 0=unsigned, anything else=returned/cancelled/etc.
                // statusType 3 ("Uploaded") ignores the signatory chain entirely — it's just
                // documents this person owns (document_attach.doc_eid/doc_eid_user_type).
                var queryParams = new
                {
                    Eid = eid,
                    UserType = userType,
                    StatusType = statusType,
                    DocTypeId = docTypeId,
                    Year = year,
                    StartDate = startDate ?? "",
                    EndDate = endDate ?? "",
                };

                const string dateFilterSql = @"
                    AND (@Year IS NULL OR b.doc_datetime LIKE '%' + CAST(@Year AS NVARCHAR(20)) + '%')
                    AND (
                        @Year IS NOT NULL OR @StartDate = '' OR @EndDate = ''
                        OR (
                            CASE WHEN ISDATE(b.doc_datetime) = 1 THEN CAST(CONVERT(datetime, b.doc_datetime, 100) AS DATE) END
                            BETWEEN CAST(@StartDate AS DATE) AND CAST(@EndDate AS DATE)
                        )
                    )";

                string sql;
                if (statusType == 3)
                {
                    sql = $@"
                        SELECT DISTINCT
                            b.doc_id AS DocId, b.doc_name AS DocName, b.doc_type AS DocType,
                            b.doc_description AS DocDescription, b.doc_status_id AS DocStatusId,
                            b.doc_eid AS DocEid, b.doc_code AS DocCode, b.doc_signatory_type AS DocSignatoryType,
                            b.doc_type_id AS DocTypeId, b.doc_eid_user_type AS DocEidUserType,
                            ISNULL(b.doc_is, 0) AS DocIs, d.document_description AS DocTypeDesc,
                            e.status_type AS StatusType, ISNULL(c.fname, '') AS Preparer,
                            ISNULL(c.office_name, '') AS Office,
                            CASE WHEN ISDATE(b.doc_datetime) = 1 THEN CONVERT(datetime, b.doc_datetime, 100) END AS DbDatetime
                        FROM [bacpdfsign].[dbo].[document_attach] b
                        LEFT JOIN [bacpdfsign].[dbo].[signatory_names] c
                            ON b.doc_eid = c.eid AND c.user_type = b.doc_eid_user_type
                        LEFT JOIN [bacpdfsign].[dbo].[document_types] d ON b.doc_type_id = d.id
                        LEFT JOIN [bacpdfsign].[dbo].[req_status] e ON b.doc_status_id = e.id
                        WHERE b.doc_eid = @Eid AND b.doc_eid_user_type = @UserType
                            AND (@DocTypeId IS NULL OR b.doc_type_id = @DocTypeId)
                            {dateFilterSql}
                        ORDER BY b.doc_id DESC";
                }
                else
                {
                    sql = $@"
                        SELECT DISTINCT
                            b.doc_id AS DocId, b.doc_name AS DocName, b.doc_type AS DocType,
                            b.doc_description AS DocDescription, b.doc_status_id AS DocStatusId,
                            b.doc_eid AS DocEid, b.doc_code AS DocCode, b.doc_signatory_type AS DocSignatoryType,
                            b.doc_type_id AS DocTypeId, b.doc_eid_user_type AS DocEidUserType,
                            ISNULL(b.doc_is, 0) AS DocIs, d.document_description AS DocTypeDesc,
                            e.status_type AS StatusType, ISNULL(c.fname, '') AS Preparer,
                            ISNULL(c.office_name, '') AS Office, sd.SignDatetime AS DbDatetime
                        FROM [bacpdfsign].[dbo].[document_attach] b
                        INNER JOIN [bacpdfsign].[dbo].[document_signatories] f ON b.doc_id = f.doc_id
                        LEFT JOIN [bacpdfsign].[dbo].[signatory_names] c ON b.doc_eid = c.eid AND c.user_type = 0
                        LEFT JOIN [bacpdfsign].[dbo].[document_types] d ON b.doc_type_id = d.id
                        LEFT JOIN [bacpdfsign].[dbo].[req_status] e ON b.doc_status_id = e.id
                        OUTER APPLY (
                            -- sign_datetime is nvarchar, not native datetime — must ISDATE-guard before
                            -- converting or MAX() sorts alphabetically instead of chronologically.
                            SELECT MAX(CASE WHEN ISDATE(g.sign_datetime) = 1 THEN CONVERT(datetime, g.sign_datetime, 100) END) AS SignDatetime
                            FROM [bacpdfsign].[dbo].[document_signature_location] g
                            WHERE g.sig_id = f.sig_id
                        ) sd
                        WHERE f.sig_eid = @Eid AND f.sig_user_type = @UserType
                            AND (
                                (@StatusType = 1 AND f.sig_status = 1) OR
                                (@StatusType = 0 AND f.sig_status = 0) OR
                                (@StatusType = 2 AND f.sig_status NOT IN (0, 1))
                            )
                            AND (@DocTypeId IS NULL OR b.doc_type_id = @DocTypeId)
                            {dateFilterSql}
                        ORDER BY b.doc_id DESC";
                }

                var results = await _dbService.QueryAsync<DocListDto, dynamic>(
                    sql, queryParams, CommandType.Text);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting document history for eid: {Eid}, statusType: {StatusType}", eid, statusType);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching document history", error = ex.Message });
            }
        }

        [HttpGet("get_returned_history")]
        public async Task<IActionResult> GetReturnedHistory(
            [FromQuery] int eid,
            [FromQuery] int userType,
            [FromQuery] string startDate,
            [FromQuery] string endDate,
            [FromQuery] int? docTypeId = null)
        {
            try
            {
                var sql = @"
                    ;WITH PreviousSignature AS (
                        SELECT 
                            ds_current.sig_id,
                            ds_current.doc_id,
                            ds_current.sig_order,
                            COALESCE(
                                MAX(dsl_prev.sign_datetime),
                                CASE WHEN ISDATE(da.doc_datetime) = 1 THEN CONVERT(datetime, da.doc_datetime, 100) END
                            ) AS previous_event_datetime
                        FROM [bacpdfsign].[dbo].[document_signatories] ds_current
                        INNER JOIN [bacpdfsign].[dbo].[document_attach] da 
                            ON ds_current.doc_id = da.doc_id
                        LEFT JOIN [bacpdfsign].[dbo].[document_signatories] ds_prev 
                            ON ds_current.doc_id = ds_prev.doc_id 
                            AND ds_prev.sig_order < ds_current.sig_order
                        LEFT JOIN [bacpdfsign].[dbo].[document_signature_location] dsl_prev 
                            ON ds_prev.sig_id = dsl_prev.sig_id
                        WHERE dsl_prev.sign_datetime IS NOT NULL OR ds_prev.sig_id IS NULL
                        GROUP BY ds_current.sig_id, ds_current.doc_id, ds_current.sig_order, da.doc_datetime
                    ),
                    LatestReturn AS (
                        SELECT 
                            sig_id,
                            datentime,
                            reasons,
                            return_type,
                            ROW_NUMBER() OVER (PARTITION BY sig_id ORDER BY datentime DESC) AS rn
                        FROM [bacpdfsign].[dbo].[document_signatory_return]
                        WHERE datentime IS NOT NULL
                    )
                    SELECT 
                        da.doc_id AS DocId,
                        da.doc_name AS DocName,
                        da.doc_code AS DocCode,
                        da.doc_description AS DocDescription,
                        da.doc_datetime AS DocCreatedDatetime,
                        creator.fname AS DocCreatedByName,
                        creator.position AS DocCreatedByPosition,
                        creator.office_name AS DocCreatedByOffice,
                        da.doc_status_id AS DocStatusId,
                        rs.status_type AS DocStatusName,
                        dt.document_description AS DocTypeName,
                        dt.document_abbr AS DocTypeAbbr,
                        ds.sig_id AS SigId,
                        ds.sig_order AS SigOrder,
                        ds.sig_eid AS SigEid,
                        ds.sig_user_type AS SigUserType,
                        signatory.fname AS SignatoryName,
                        signatory.position AS SignatoryPosition,
                        signatory.office_name AS SignatoryOffice,
                        ds.sig_status AS SigStatus,
                        rss.description AS SigStatusName,
                        ds.date_time_inserted AS SignatoryAssignedDatetime,
                        ret.datentime AS UserActionDatetime,
                        ret.reasons AS UserActionReason,
                        ret.return_type AS ReturnType,
                        ps.previous_event_datetime AS PreviousEventDatetime,
                        CASE 
                            WHEN ret.datentime IS NOT NULL AND ps.previous_event_datetime IS NOT NULL THEN
                                CONCAT(
                                    DATEDIFF(day, ps.previous_event_datetime, ret.datentime), 'd ',
                                    FORMAT(DATEADD(second, 
                                        DATEDIFF(second, ps.previous_event_datetime, ret.datentime) % 86400, 
                                        0), 'HH:mm:ss')
                                )
                            ELSE '0d 00:05:00'
                        END AS TimeSincePrevious
                    FROM 
                        [bacpdfsign].[dbo].[document_attach] da
                        INNER JOIN [bacpdfsign].[dbo].[document_types] dt 
                            ON da.doc_type_id = dt.id
                        INNER JOIN [bacpdfsign].[dbo].[req_status] rs 
                            ON da.doc_status_id = rs.id
                        INNER JOIN [bacpdfsign].[dbo].[document_signatories] ds 
                            ON da.doc_id = ds.doc_id
                        INNER JOIN [bacpdfsign].[dbo].[req_sign_status] rss 
                            ON ds.sig_status = rss.id
                        LEFT JOIN [bacpdfsign].[dbo].[signatory_names] creator 
                            ON da.doc_eid = creator.eid AND da.doc_eid_user_type = creator.user_type
                        LEFT JOIN [bacpdfsign].[dbo].[signatory_names] signatory 
                            ON ds.sig_eid = signatory.eid AND ds.sig_user_type = signatory.user_type
                        LEFT JOIN PreviousSignature ps 
                            ON ds.sig_id = ps.sig_id
                        LEFT JOIN LatestReturn ret 
                            ON ds.sig_id = ret.sig_id AND ret.rn = 1
                    WHERE 
                        ds.sig_eid = @Eid
                        AND ds.sig_user_type = @UserType
                        AND ds.sig_status NOT IN (0, 1)
                        AND da.doc_status_id IN (4, 5, 7, 8, 9, 10, 11, 13)
                        AND NOT EXISTS (
                            SELECT 1
                            FROM [bacpdfsign].[dbo].[document_signatories] ds2
                            WHERE ds2.doc_id = ds.doc_id
                              AND ds2.sig_order < ds.sig_order
                              AND ds2.sig_status != 1
                        )
                        AND CASE WHEN ISDATE(da.doc_datetime) = 1 THEN CONVERT(datetime, da.doc_datetime, 100) END BETWEEN @StartDate AND @EndDate
                        AND (@DocTypeId IS NULL OR da.doc_type_id = @DocTypeId)
                    ORDER BY
                        CASE WHEN ISDATE(da.doc_datetime) = 1 THEN CONVERT(datetime, da.doc_datetime, 100) END DESC";

                var results = await _dbService.QueryAsync<ReturnedHistoryDto, dynamic>(
                    sql,
                    new { Eid = eid, UserType = userType, StartDate = startDate, EndDate = endDate, DocTypeId = docTypeId },
                    CommandType.Text);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting returned history for eid: {Eid}", eid);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching returned history", error = ex.Message });
            }
        }


        // ==================== GET DOCUMENT VIEW (details + enriched signatory timeline) ====================
        //
        // Feeds the My Documents expand row: document header (item 1), plus a per-signatory
        // "received" date and day-count (item 2), plus a countersign annotation (item 3).
        // The day-count is received-to-signed (turnaround time) once a signatory has signed,
        // or received-to-now (still waiting) while they haven't.
        //
        // "Received" is computed per sig_order group rather than off doc_status_id directly:
        // the first group receives the doc on doc_datetime; every later group receives it when
        // the immediately preceding group's *last* member signed (MAX(sign_datetime) across that
        // group). This produces the same result as "doc_status_id == 1 -> doc_datetime" for the
        // first group while still being correct for docs already on their 2nd/3rd sig_order group.
        //
        // Countersign is only resolved when a sig_order group has exactly 2 members (per business
        // rule) — larger groups are ambiguous about who actually signed for whom, so are left blank.
        [HttpGet("get_document_view")]
        public async Task<IActionResult> GetDocumentView([FromQuery] int docId)
        {
            try
            {
                var result = await _signingService.GetDocumentViewAsync(docId);
                if (result?.Document == null)
                    return NotFound(new { success = false, message = "Document not found" });

                return Ok(new { document = result.Document, signatories = result.Signatories, grouped = result.Grouped });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting document view for docId: {DocId}", docId);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching document view", error = ex.Message });
            }
        }

        // ==================== GET DOCUMENTS FOR SIGNATORY (list + this signatory's own status) ====================
        //
        // List version of get_document_view: every document where (eid, userType) appears as a
        // signatory, each row carrying document details plus ONLY that signatory's own single
        // data point — sig_status, receivedDatetime, signDatetime, and daysPending (turnaround
        // time once signed, else received-to-now while still pending). Filtered by eid + userType
        // (required) and docTypeId (optional — null/omitted returns all document types).
        //
        // Reuses the exact same "received" cascade and day-count rules as get_document_view:
        // a document's full signatory chain still has to be walked to know what this signatory's
        // own "received" moment was, even though only their single row is returned.
        [HttpGet("get_documents_for_signatory")]
        public async Task<IActionResult> GetDocumentsForSignatory(
            [FromQuery] int eid,
            [FromQuery] int userType,
            [FromQuery] int? docTypeId = null,
            [FromQuery] int? statusType = null,
            [FromQuery] int? year = null)
        {
            if (statusType.HasValue && (statusType < 0 || statusType > 3))
                return BadRequest(new { message = "statusType must be 0 (unsigned), 1 (signed), 2 (returned/cancelled), or 3 (uploaded)." });

            try
            {
                // statusType 3 ("Uploaded") bypasses the signatory chain entirely — it's just
                // documents this person owns (document_attach.doc_eid/doc_eid_user_type) — so
                // there's no per-signatory received/signed/days data to compute. Year here
                // filters on doc_datetime (the document's own creation date).
                if (statusType == 3)
                {
                    var uploadedRows = await _dbService.QueryAsync<SignatoryDocumentListItemDto, dynamic>(
                        @"SELECT
                            da.doc_id AS DocId, da.doc_name AS DocName, da.doc_code AS DocCode,
                            da.doc_description AS DocDescription, da.doc_status_id AS DocStatusId,
                            rs.status_type AS DocStatusName, da.doc_type_id AS DocTypeId,
                            dt.document_description AS DocTypeName, dt.document_abbr AS DocTypeAbbr,
                            da.doc_datetime AS DocDatetime, da.doc_pages AS DocPages,
                            0 AS SigId, 0 AS SigOrder, 0 AS SigStatus, CAST(NULL AS NVARCHAR(50)) AS SigStatusName
                          FROM [bacpdfsign].[dbo].[document_attach] da
                          LEFT JOIN [bacpdfsign].[dbo].[document_types] dt ON dt.id = da.doc_type_id
                          LEFT JOIN [bacpdfsign].[dbo].[req_status] rs ON rs.id = da.doc_status_id
                          WHERE da.doc_eid = @Eid AND da.doc_eid_user_type = @UserType
                            AND (@DocTypeId IS NULL OR da.doc_type_id = @DocTypeId)
                            AND (
                                @Year IS NULL OR
                                (ISDATE(da.doc_datetime) = 1 AND YEAR(CONVERT(datetime, da.doc_datetime, 100)) = @Year)
                            )
                          ORDER BY da.doc_id DESC",
                        new { Eid = eid, UserType = userType, DocTypeId = docTypeId, Year = year },
                        CommandType.Text);

                    return Ok(uploadedRows);
                }

                var targetRows = (await _dbService.QueryAsync<SignatoryDocumentListItemDto, dynamic>(
                    @"SELECT
                        da.doc_id AS DocId, da.doc_name AS DocName, da.doc_code AS DocCode,
                        da.doc_description AS DocDescription, da.doc_status_id AS DocStatusId,
                        rs.status_type AS DocStatusName, da.doc_type_id AS DocTypeId,
                        dt.document_description AS DocTypeName, dt.document_abbr AS DocTypeAbbr,
                        da.doc_datetime AS DocDatetime, da.doc_pages AS DocPages,
                        ds.sig_id AS SigId, ds.sig_order AS SigOrder, ds.sig_status AS SigStatus,
                        rss.description AS SigStatusName
                      FROM [bacpdfsign].[dbo].[document_signatories] ds
                      INNER JOIN [bacpdfsign].[dbo].[document_attach] da ON da.doc_id = ds.doc_id
                      LEFT JOIN [bacpdfsign].[dbo].[document_types] dt ON dt.id = da.doc_type_id
                      LEFT JOIN [bacpdfsign].[dbo].[req_status] rs ON rs.id = da.doc_status_id
                      INNER JOIN [bacpdfsign].[dbo].[req_sign_status] rss ON rss.id = ds.sig_status
                      WHERE ds.sig_eid = @Eid AND ds.sig_user_type = @UserType
                        AND (@DocTypeId IS NULL OR da.doc_type_id = @DocTypeId)
                        AND (
                            @StatusType IS NULL OR
                            (@StatusType = 1 AND ds.sig_status = 1) OR
                            (@StatusType = 0 AND ds.sig_status = 0) OR
                            (@StatusType = 2 AND ds.sig_status NOT IN (0, 1))
                        )
                      ORDER BY da.doc_id DESC",
                    new { Eid = eid, UserType = userType, DocTypeId = docTypeId, StatusType = statusType },
                    CommandType.Text)).ToList();

                if (targetRows.Count == 0)
                    return Ok(Array.Empty<SignatoryDocumentListItemDto>());

                // Filtered via a nested subquery (not an `IN @list` C# parameter list) because
                // a signatory tied to thousands of documents can produce enough sig_ids to blow
                // past SQL Server's 2100-parameter cap when Dapper expands `IN @List`.
                const string docIdsForEidSql = @"
                      SELECT ds.doc_id
                      FROM [bacpdfsign].[dbo].[document_signatories] ds
                      INNER JOIN [bacpdfsign].[dbo].[document_attach] da ON da.doc_id = ds.doc_id
                      WHERE ds.sig_eid = @Eid AND ds.sig_user_type = @UserType
                        AND (@DocTypeId IS NULL OR da.doc_type_id = @DocTypeId)";

                var queryParams = new { Eid = eid, UserType = userType, DocTypeId = docTypeId };

                // Every signatory (all orders, all people) for these documents — needed to
                // walk each document's chain up to the target row's own sig_order.
                var allSignatories = (await _dbService.QueryAsync<AllSignatoryOrderDto, dynamic>(
                    $@"SELECT doc_id AS DocId, sig_id AS SigId, sig_order AS SigOrder
                      FROM [bacpdfsign].[dbo].[document_signatories]
                      WHERE doc_id IN ({docIdsForEidSql})",
                    queryParams, CommandType.Text)).ToList();

                var signDates = (await _dbService.QueryAsync<SigSignDateDto, dynamic>(
                    // sign_datetime is nvarchar, not a native datetime column — must convert
                    // before aggregating or MAX() sorts alphabetically instead of chronologically.
                    $@"SELECT sig_id AS SigId,
                             MAX(CASE WHEN ISDATE(sign_datetime) = 1 THEN CONVERT(datetime, sign_datetime, 100) END) AS SignDatetime
                      FROM [bacpdfsign].[dbo].[document_signature_location]
                      WHERE sig_id IN (
                          SELECT sig_id FROM [bacpdfsign].[dbo].[document_signatories]
                          WHERE doc_id IN ({docIdsForEidSql})
                      )
                      GROUP BY sig_id",
                    queryParams, CommandType.Text))
                    .ToDictionary(s => s.SigId, s => s.SignDatetime);

                var docDatetimeParsed = new Dictionary<int, DateTime?>();
                foreach (var r in targetRows)
                    if (!docDatetimeParsed.ContainsKey(r.DocId))
                        docDatetimeParsed[r.DocId] = DateTime.TryParse(r.DocDatetime, out var dt) ? dt : null;

                var signatoriesByDoc = allSignatories.GroupBy(s => s.DocId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SigOrder).ToList());

                foreach (var row in targetRows)
                {
                    if (!signatoriesByDoc.TryGetValue(row.DocId, out var docSignatories))
                        continue;

                    // Same cascade as get_document_view: seed with doc_datetime, then walk
                    // sig_order groups in order, advancing to each group's max sign date.
                    DateTime? previousGroupMaxSign = docDatetimeParsed.TryGetValue(row.DocId, out var dd) ? dd : null;

                    foreach (var group in docSignatories.GroupBy(s => s.SigOrder).OrderBy(g => g.Key))
                    {
                        if (group.Key == row.SigOrder)
                        {
                            row.ReceivedDatetime = previousGroupMaxSign;
                            break;
                        }

                        var signedInGroup = group
                            .Where(s => signDates.TryGetValue(s.SigId, out var sd) && sd.HasValue)
                            .Select(s => signDates[s.SigId]!.Value)
                            .ToList();
                        if (signedInGroup.Count > 0)
                            previousGroupMaxSign = signedInGroup.Max();
                    }

                    if (signDates.TryGetValue(row.SigId, out var ownSign))
                        row.SignDatetime = ownSign;

                    if (row.ReceivedDatetime.HasValue)
                    {
                        var until = row.SignDatetime ?? DateTime.Now;
                        row.DaysPending = (until.Date - row.ReceivedDatetime.Value.Date).Days;
                    }
                }

                // Year filter applies to whichever date the row actually has — received or
                // signed — since ReceivedDatetime/SignDatetime only exist after the cascade
                // above, this has to run as a final in-memory pass rather than in SQL.
                if (year.HasValue)
                {
                    targetRows = targetRows
                        .Where(r => r.ReceivedDatetime?.Year == year || r.SignDatetime?.Year == year)
                        .ToList();
                }

                return Ok(targetRows);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting documents for signatory eid: {Eid}, userType: {UserType}", eid, userType);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching documents for signatory", error = ex.Message });
            }
        }

        // ==================== GET DASHBOARD SUMMARY (org-wide totals + one signatory's own breakdown) ====================
        //
        // "organization" is unfiltered by person — every document in the system for the given
        // year, bucketed by document_attach.doc_status_id (the document's own overall status).
        // "personal" reuses the exact sig_status semantics from get_documents_for_signatory /
        // get_signed_history: 1=signed, 0=unsigned, anything else=returned/cancelled, plus a
        // separate "uploaded" count from document_attach.doc_eid/doc_eid_user_type.
        [HttpGet("get_dashboard_summary")]
        public async Task<IActionResult> GetDashboardSummary(
            [FromQuery] int eid,
            [FromQuery] int userType,
            [FromQuery] int? year = null)
        {
            try
            {
                const string yearFilterSql = @"
                    AND (@Year IS NULL OR (ISDATE(da.doc_datetime) = 1 AND YEAR(CONVERT(datetime, da.doc_datetime, 100)) = @Year))";

                var orgBuckets = (await _dbService.QueryAsync<DashboardStatusBucketDto, dynamic>(
                    $@"SELECT
                        CASE
                            WHEN da.doc_status_id = 2 THEN 'Signed'
                            WHEN da.doc_status_id = 1 THEN 'Unsigned'
                            WHEN da.doc_status_id IN (4, 5, 7, 8, 9, 11, 13) THEN 'Cancelled / Returned'
                            ELSE 'In Progress'
                        END AS Label,
                        COUNT(DISTINCT da.doc_id) AS Count
                      FROM [bacpdfsign].[dbo].[document_attach] da
                      WHERE 1 = 1
                        {yearFilterSql}
                      GROUP BY
                        CASE
                            WHEN da.doc_status_id = 2 THEN 'Signed'
                            WHEN da.doc_status_id = 1 THEN 'Unsigned'
                            WHEN da.doc_status_id IN (4, 5, 7, 8, 9, 11, 13) THEN 'Cancelled / Returned'
                            ELSE 'In Progress'
                        END",
                    new { Year = year }, CommandType.Text)).ToList();

                var orgTotal = orgBuckets.Sum(b => b.Count);

                var personal = (await _dbService.QueryFirstOrDefaultAsync<DashboardPersonalSummaryDto, dynamic>(
                    $@"SELECT
                        SUM(CASE WHEN ds.sig_status = 1 THEN 1 ELSE 0 END) AS Signed,
                        SUM(CASE WHEN ds.sig_status = 0 THEN 1 ELSE 0 END) AS Unsigned,
                        SUM(CASE WHEN ds.sig_status NOT IN (0, 1) THEN 1 ELSE 0 END) AS Returned
                      FROM [bacpdfsign].[dbo].[document_signatories] ds
                      INNER JOIN [bacpdfsign].[dbo].[document_attach] da ON da.doc_id = ds.doc_id
                      WHERE ds.sig_eid = @Eid AND ds.sig_user_type = @UserType
                        {yearFilterSql}",
                    new { Eid = eid, UserType = userType, Year = year }, CommandType.Text))
                    ?? new DashboardPersonalSummaryDto();

                personal.Uploaded = await _dbService.ExecuteScalarAsync<int, dynamic>(
                    $@"SELECT COUNT(*)
                      FROM [bacpdfsign].[dbo].[document_attach] da
                      WHERE da.doc_eid = @Eid AND da.doc_eid_user_type = @UserType
                        {yearFilterSql}",
                    new { Eid = eid, UserType = userType, Year = year }, CommandType.Text);

                return Ok(new
                {
                    year,
                    organization = new { total = orgTotal, byStatus = orgBuckets },
                    personal = new
                    {
                        eid,
                        userType,
                        total = personal.Signed + personal.Unsigned + personal.Returned + personal.Uploaded,
                        signed = personal.Signed,
                        unsigned = personal.Unsigned,
                        returned = personal.Returned,
                        uploaded = personal.Uploaded,
                    },
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting dashboard summary for eid: {Eid}, userType: {UserType}", eid, userType);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching dashboard summary", error = ex.Message });
            }
        }

        // ==================== GET DOCUMENT SIGNATORIES (legacy — superseded by get_document_view above) ====================

        [HttpGet("get_document_signatories")]
        public async Task<IActionResult> GetDocumentSignatories([FromQuery] int docId)
        {
            try
            {
                var sql = @"
                    SELECT
                        ds.sig_id       AS SigId,
                        ds.doc_id       AS DocId,
                        ds.sig_code     AS SigCode,
                        ds.sig_eid      AS SigEid,
                        ds.sig_status   AS SigStatus,
                        ds.sig_order    AS SigOrder,
                        ds.sig_remarks  AS SigRemarks,
                        ds.sig_user_type AS SigUserType,
                        ds.sig_level    AS SigLevel,
                        ds.sig_sign_count AS SigSignCount,
                        ds.sig_remarks_datenTime AS SigRemarksDatenTime,
                        ds.date_time_inserted AS DateTimeInserted,
                        rss.id          AS SignStatusId,
                        rss.description AS SignStatusDescription,
                        sn.fname        AS Fname,
                        sn.position     AS Position,
                        sn.office_name  AS OfficeName
                    FROM [bacpdfsign].[dbo].[document_signatories] ds
                    INNER JOIN [bacpdfsign].[dbo].[req_sign_status] rss
                        ON ds.sig_status = rss.id
                    LEFT JOIN [bacpdfsign].[dbo].[signatory_names] sn
                        ON ds.sig_eid = sn.eid AND ds.sig_user_type = sn.user_type
                    WHERE ds.doc_id = @DocId
                    ORDER BY ds.sig_order, ds.date_time_inserted";

                var signatories = (await _dbService.QueryAsync<DocumentSignatoryDto, dynamic>(
                    sql, new { DocId = docId }, CommandType.Text)).ToList();

                if (signatories.Count == 0)
                    return Ok(new { signatories = Array.Empty<object>(), grouped = Array.Empty<object>() });

                // Group by sig_order for the hierarchical view
                var grouped = signatories
                    .GroupBy(s => s.SigOrder)
                    .OrderBy(g => g.Key)
                    .Select(g => new DocumentSignatoryGroupDto
                    {
                        SigOrder = g.Key,
                        SignatoryCount = g.Count(),
                        Signatories = g.ToList()
                    })
                    .ToList();

                return Ok(new
                {
                    signatories,
                    grouped
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting document signatories for docId: {DocId}", docId);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching document signatories", error = ex.Message });
            }
        }

        // ==================== SAVE SIGNATURE IMAGE (Converted from legacy ASP.NET) ====================

        [HttpPost("save_signature_image")]
        public async Task<IActionResult> SaveSignatureImage([FromBody] SaveSignatureRequest request)
        {
            var result = await _signingService.SaveSignatureImageAsync(request);

            if (result.Success)
            {
                return Ok(new
                {
                    success = true,
                    docId = result.DocId,
                    type = "Document",
                    nextSignatoryEid = result.NextSignatoryEid,
                    docCode = result.DocCode,
                    nextSignatoryUserType = result.NextSignatoryUserType,
                    signerEid = result.SignerEid,
                    signerUserType = result.SignerUserType,
                    legacyResult = result.LegacyResult
                });
            }

            if (result.IsServerError)
                return StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message, debugError = result.DebugError });

            return BadRequest(new { success = false, message = result.Message });
        }

        // ==================== GET PDF DIGITAL ONLY (Converted from legacy ASP.NET) ====================

        [HttpGet("get_pdf_digital_only")]
        public async Task<IActionResult> GetPdfDigitalOnly(
           [FromQuery] int formId,
           [FromQuery] int isDownload = 0)
        {
            try
            {
                var result = await _signingService.GetPdfDigitalOnlyAsync(formId);

                if (result.NotFoundMessage != null)
                    return NotFound(new { success = false, message = result.NotFoundMessage });

                if (result.BadRequestMessage != null)
                    return BadRequest(new { success = false, message = result.BadRequestMessage });

                var disposition = isDownload == 1 ? "attachment" : "inline";
                Response.Headers.Append("Content-Disposition", $"{disposition}; filename=\"{WebUtility.UrlEncode(result.FileName)}\"");
                Response.Headers.Append("Cache-Control", "no-store");
                Response.Headers.Append("X-Content-Type-Options", "nosniff");

                return File(result.PdfBytes!, "application/pdf");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting PDF for formId: {FormId}", formId);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching the PDF", error = ex.Message });
            }
        }

        // ==================== PUBLIC DOCUMENT VIEW LINK ====================

        // Mints a no-login, read-only link to this one document — the same
        // AES-GCM token mechanism as the public signing links (see
        // PublicSignController), scope="view" instead of "sign". The link
        // grants view access to exactly this docId and nothing else; it is
        // NOT tied to any eid/userType and does not grant reconstruct access
        // (PublicSignController has no public reconstruct endpoint).
        [HttpPost("create_document_view_link/{docId:int}")]
        public IActionResult CreateDocumentViewLink(int docId, [FromQuery] int? ttlMinutes = null)
        {
            var payload = new PublicSignTokenPayload
            {
                DocId = docId,
                Eid = 0,
                UserType = 0,
                Exp = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes ?? 60 * 24 * 7).ToUnixTimeSeconds(), // 7 days
                Jti = Guid.NewGuid().ToString(),
                Scope = "view"
            };

            var token = _aesTokenService.Encrypt(payload);
            var baseUrl = (_configuration["PublicSign:BaseUrl"] ?? "").TrimEnd('/');
            var link = $"{baseUrl}/public/document-view?t={Uri.EscapeDataString(token)}";

            return Ok(new { link, expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp) });
        }

        // ==================== RECONSTRUCT PDF (ported from legacy getPDF_View_old) ====================

        // Force-reloads the currently-saved signed PDF and redraws every
        // already-applied signature stamp from scratch. Admin-only, same as
        // admin_update_document/admin_update_signatories above — it rewrites
        // the document's saved file in place. SigningService itself also
        // refuses to reconstruct a returned/cancelled/terminated document
        // (see NonReconstructableStatuses).
        [HttpPost("reconstruct_pdf/{docId:int}")]
        public async Task<IActionResult> ReconstructPdf(int docId)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            try
            {
                var result = await _signingService.ReconstructSignedPdfAsync(docId);

                if (result.NotFoundMessage != null)
                    return NotFound(new { success = false, message = result.NotFoundMessage });

                if (result.BadRequestMessage != null)
                    return BadRequest(new { success = false, message = result.BadRequestMessage });

                if (result.ServerErrorMessage != null)
                    return StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.ServerErrorMessage });

                Response.Headers.Append("Cache-Control", "no-store");
                Response.Headers.Append("X-Content-Type-Options", "nosniff");
                return File(result.PdfBytes!, "application/pdf");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconstructing PDF for docId: {DocId}", docId);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while reconstructing the PDF", error = ex.Message });
            }
        }

        // Add this private helper method to fix CS0103: The name 'ExtractCertificateFromPfx' does not exist in the current context

        private static X509Certificate2? ExtractCertificateFromPfx(byte[]? pfxBytes)
        {
            if (pfxBytes == null || pfxBytes.Length == 0)
                return null;

            try
            {
                var collection = new X509Certificate2Collection();
                collection.Import(pfxBytes, null, X509KeyStorageFlags.EphemeralKeySet);

                // Return the first certificate found (without private key)
                return collection.Count > 0 ? collection[0] : null;
            }
            catch
            {
                return null;
            }
        }



        // ==================== GET SIGNATURE IMAGE (converted from legacy ASP.NET) ====================

        [HttpGet("get_signature_image_merged")]
        public async Task<IActionResult> GetSignatureImageMerged(
            [FromQuery] string eids,
            [FromQuery] string usertypes,
            [FromQuery] string type = "signature")
        {
            var result = await _signingService.GetSignatureImageMergedAsync(eids, usertypes, type);

            if (result.InvalidType)
                return BadRequest(new { message = "Invalid type parameter. Use 'signature' or 'initial'." });

            if (result.NotFound || result.Bytes == null)
                return NotFound();

            return File(result.Bytes, result.ContentType ?? "image/png");
        }

        // Replaces the signature/initial specimen on the user's existing
        // active certificate row (pfx_attachments / pfx_initial_signature) —
        // converted from the legacy save_newSpicimen action. Unlike
        // generate_certificate/submit_certificate_request, this does not
        // touch the PFX or create a new certificate_registration row.
        [HttpPost("update_signature_specimen")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UpdateSignatureSpecimen([FromForm] UpdateSignatureSpecimenForm form)
        {
            if (form.Type != "signature" && form.Type != "initial")
                return BadRequest(new { success = false, message = "Type must be 'signature' or 'initial'." });
            if (form.Image == null || form.Image.Length == 0)
                return BadRequest(new { success = false, message = "A specimen image is required." });

            string base64;
            using (var ms = new MemoryStream())
            {
                await form.Image.CopyToAsync(ms);
                base64 = $"data:{form.Image.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
            }

            var sql = form.Type == "signature"
                ? "UPDATE bacpdfsign.dbo.pfx_attachments SET signatures = @Base64 WHERE eid = @Eid AND user_type = @UserType AND active = 1"
                : "UPDATE bacpdfsign.dbo.pfx_initial_signature SET initial_signature = @Base64 WHERE eid = @Eid AND user_type = @UserType AND active = 1";

            var affected = await _dbService.ExecuteAsync(
                sql,
                new { Base64 = base64, Eid = form.Eid, UserType = form.UserType },
                CommandType.Text
            );

            if (affected == 0)
                return NotFound(new { success = false, message = "No active certificate found for this user." });

            await _dbService.ExecuteAsync(
                @"INSERT INTO bacpdfsign.dbo.signature_specimen_history (eid, user_type, specimen_type)
                  VALUES (@Eid, @UserType, @Type)",
                new { form.Eid, form.UserType, form.Type },
                CommandType.Text
            );

            // The merged PNG is cached on disk by get_signature_image_merged
            // and only regenerated when the file is missing — delete it so
            // the next fetch picks up the row we just updated instead of
            // serving the stale specimen.
            try
            {
                var fileName = form.Type == "signature" ? $"signature_{form.Eid}.png" : $"initial_{form.Eid}.png";
                var pngPath = Path.Combine(DigitalSpicimenPath, form.UserType, form.Eid, fileName);
                using (_fileStorage.Connect(NetworkPath))
                {
                    await _fileStorage.DeleteFileAsync(pngPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate cached specimen PNG for eid: {Eid}, type: {Type}", form.Eid, form.Type);
            }

            return Ok(new { success = true });
        }


        [HttpPost("generate_certificate")]
        public async Task<IActionResult> GenerateCertificate([FromBody] GenerateCertificateRequest req)
        {
            var result = await GenerateCertificateCoreAsync(req);
            if (!result.Success)
            {
                _logger.LogError("Error inserting certificate for eid: {Eid}, userType: {UserType}: {Error}", req.Eid, req.UserType, result.ErrorMessage);
                return StatusCode((int)HttpStatusCode.InternalServerError, new
                {
                    success = false,
                    message = "An error occurred while saving the certificate.",
                    error = result.ErrorMessage
                });
            }

            return Ok(new
            {
                success = true,
                thumbprint = result.Certificate!.Thumbprint,
                serialNumber = result.Certificate.SerialNumber,
                pfxFile = $"cert_{req.Eid}.p12",
                signatureFile = result.SignatureFile,
                regId = result.RegId
            });
        }

        private class CertificateGenerationResult
        {
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
            public X509Certificate2? Certificate { get; set; }
            public int RegId { get; set; }
            public string? SignatureFile { get; set; }
        }

        // Shared by generate_certificate (JSON, no valid IDs) and
        // submit_certificate_request (multipart, saves valid IDs too) — both
        // just need a BouncyCastle-generated .p12 written to the NAS and
        // inserted as a pending (user_status/active = 0) registration.
        private async Task<CertificateGenerationResult> GenerateCertificateCoreAsync(GenerateCertificateRequest req)
        {
            var random = new SecureRandom();
            var certificateGenerator = new X509V3CertificateGenerator();
            var serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random);
            certificateGenerator.SetSerialNumber(serialNumber);

            certificateGenerator.SetIssuerDN(new X509Name("O=Provincial Government of Agusan del Sur, CN=PGAS"));
            certificateGenerator.SetSubjectDN(new X509Name($"O={req.Organisation},CN={req.FullName},E={req.Email},C={req.TwoLetter},ST={req.Province},L={req.Locality},UID={req.Eid}"));
            certificateGenerator.SetNotBefore(DateTime.UtcNow.Date);
            certificateGenerator.SetNotAfter(DateTime.UtcNow.Date.AddYears(5));

            const int strength = 2048;
            var keyGenerationParameters = new KeyGenerationParameters(random, strength);
            var keyPairGenerator = new RsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenerationParameters);

            var subjectKeyPair = keyPairGenerator.GenerateKeyPair();
            certificateGenerator.SetPublicKey(subjectKeyPair.Public);

            var issuerKeyPair = subjectKeyPair;
            const string signatureAlgorithm = "SHA256WithRSA";
            var signatureFactory = new Asn1SignatureFactory(signatureAlgorithm, issuerKeyPair.Private);
            var bouncyCert = certificateGenerator.Generate(signatureFactory);

            // Convert to X509Certificate2
            X509Certificate2 certificate;
            var store = new Pkcs12StoreBuilder().Build();
            store.SetKeyEntry($"{req.FullName}_key", new AsymmetricKeyEntry(subjectKeyPair.Private), new[] { new X509CertificateEntry(bouncyCert) });
            string exportpw = Guid.NewGuid().ToString("x");

            using (var ms = new MemoryStream())
            {
                store.Save(ms, exportpw.ToCharArray(), random);
                certificate = new X509Certificate2(ms.ToArray(), exportpw, X509KeyStorageFlags.Exportable);
            }

            // Save PFX and PNG
            var networkPath = _fileStorage.RootPath;
            var credentials = _credentialService.GetNetworkCredential();
            var basePath = DigitalSpicimenPath;
            var userTypeFolder = Path.Combine(basePath, req.UserType);
            var eidFolder = Path.Combine(userTypeFolder, req.Eid);
            var pfxPath = Path.Combine(eidFolder, $"cert_{req.Eid}.p12");
            var pfxBase64Path = Path.Combine(eidFolder, $"cert_{req.Eid}.b64");
            var pngPath = Path.Combine(eidFolder, $"signature_{req.Eid}.png");

            using (_fileStorage.Connect(networkPath))
            {
                var pfxBytes = certificate.Export(X509ContentType.Pfx, req.Passwords);
                await _fileStorage.WriteFileAsync(pfxPath, pfxBytes);

                // Base64 backup copy of the same .p12 — not read by anything;
                // purely a human-recoverable fallback alongside the binary file.
                await _fileStorage.WriteFileAsync(pfxBase64Path, System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(pfxBytes)));

                string? signatureBase64 = req.SignatureBase64;
                string dbSignature = "";
                string? signatureFile = null;
                if (!string.IsNullOrWhiteSpace(signatureBase64))
                {
                    var regex = new Regex(@"^[\w/\:.-]+;base64,");
                    var cleanBase64 = regex.Replace(signatureBase64, string.Empty);
                    byte[] pngBytes = Convert.FromBase64String(cleanBase64);
                    await _fileStorage.WriteFileAsync(pngPath, pngBytes);
                    signatureFile = $"signature_{req.Eid}.png";
                    dbSignature = signatureBase64;
                }

                // Insert into both tables in a single SQL batch
                var encryptedPassword = SPMS.Rijndael.Encrypt(req.Passwords);
                var randomCode = Guid.NewGuid().ToString("N").Substring(0, 10);
                var serialNumberStr = certificate.SerialNumber;

                var sql = @"
insert into bacpdfsign.dbo.certificate_registration
    (user_eid, user_type, user_fname, user_email, user_organisation, user_locality, user_province, user_twoletter, user_password, user_datetime, user_status, user_cpnumber, user_otp, user_serial_number, user_pin)
values
    (@eid, @user_type, @fullname, @email, @organisation, @locality, @province, @two_letter, @pasw, getdate(), 0, @cp_numbers, @d_otp, @serialNumber, @pinCode);
declare @id int = SCOPE_IDENTITY();
update bacpdfsign.dbo.certificate_registration_attachment set reg_id = @id where reg_eid = @eid and reg_id = 0;
insert into bacpdfsign.dbo.pfx_attachments
    (pfx_attachement, passwords, signatures, eid, code, user_type, active, reg_id, pin_code)
values
    (@pfx_attachement, @pasw, @signatures, @eid, @randoms, @user_type, 0, @id, @pinCode);
select @id as reg_id;
";

                try
                {
                    var regId = await _dbService.ExecuteScalarAsync<int, dynamic>(
                        sql,
                        new
                        {
                            pfx_attachement = pfxBytes,
                            eid = req.Eid,
                            user_type = req.UserType,
                            randoms = randomCode,
                            signatures = dbSignature,
                            pasw = encryptedPassword,
                            fullname = req.FullName,
                            email = req.Email,
                            organisation = req.Organisation,
                            locality = req.Locality,
                            province = req.Province,
                            two_letter = req.TwoLetter,
                            passwords = req.Passwords,
                            cp_numbers = req.CpNumbers,
                            d_otp = req.DOtp,
                            pinCode = req.PinCode,
                            serialNumber = serialNumberStr
                        },
                        CommandType.Text
                    );

                    return new CertificateGenerationResult
                    {
                        Success = true,
                        Certificate = certificate,
                        RegId = regId,
                        SignatureFile = signatureFile
                    };
                }
                catch (Exception ex)
                {
                    return new CertificateGenerationResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }
            }
        }


        // ==================== CERTIFICATE REQUEST APPROVAL ====================

        // Membership in bacpdfsign.dbo.admin_user (by the caller's own JWT
        // claims, not a client-supplied eid/userType) is the only admin gate
        // in this app. Read from claims rather than the query string since
        // this authorizes approve/reject actions on other people's requests.
        private async Task<bool> IsCurrentUserAdminAsync()
        {
            var eidClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userTypeClaim = User.FindFirstValue("UserType");
            if (!int.TryParse(eidClaim, out var eid) || !int.TryParse(userTypeClaim, out var userType))
                return false;

            var count = await _dbService.ExecuteScalarAsync<int, dynamic>(
                "SELECT COUNT(1) FROM bacpdfsign.dbo.admin_user WHERE eid = @Eid AND user_type = @UserType",
                new { Eid = eid, UserType = userType },
                CommandType.Text
            );
            return count > 0;
        }

        [HttpGet("check_admin")]
        public async Task<IActionResult> CheckAdmin()
        {
            var isAdmin = await IsCurrentUserAdminAsync();
            return Ok(new { isAdmin });
        }

        // A user's own certificate request/upload history + specimen-change
        // history — scoped to the caller's own JWT claims rather than a
        // client-supplied eid/userType, since this is personal history (not
        // something another user's request should be able to read by just
        // changing a query param).
        [HttpGet("my_certificate_requests")]
        public async Task<IActionResult> MyCertificateRequests()
        {
            var eidClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userTypeClaim = User.FindFirstValue("UserType");
            if (string.IsNullOrEmpty(eidClaim) || string.IsNullOrEmpty(userTypeClaim))
                return Unauthorized();

            var requests = await _dbService.QueryAsync<CertificateHistoryEntryDto, dynamic>(
                @"SELECT
                      cr.reg_id AS RegId,
                      cr.user_datetime AS RequestDate,
                      cr.user_decided_datetime AS DecidedDate,
                      cr.user_status AS Status,
                      cr.reg_source AS Source,
                      cr.user_reason AS Remarks
                  FROM bacpdfsign.dbo.certificate_registration cr
                  WHERE cr.user_eid = @Eid AND cr.user_type = @UserType
                  ORDER BY cr.user_datetime DESC",
                new { Eid = eidClaim, UserType = userTypeClaim },
                CommandType.Text
            );

            var specimenChanges = await _dbService.QueryAsync<SpecimenHistoryEntryDto, dynamic>(
                @"SELECT specimen_type AS SpecimenType, changed_at AS ChangedAt
                  FROM bacpdfsign.dbo.signature_specimen_history
                  WHERE eid = @Eid AND user_type = @UserType
                  ORDER BY changed_at DESC",
                new { Eid = eidClaim, UserType = userTypeClaim },
                CommandType.Text
            );

            return Ok(new { requests, specimenChanges });
        }

        // Real counterpart of the RequestCertificate form's submit — accepts
        // the signature specimen + valid ID images as actual files (unlike
        // generate_certificate, which only takes a base64 signature string
        // and has no concept of valid IDs). Reuses the same BouncyCastle
        // cert-gen/pending-insert logic via GenerateCertificateCoreAsync.
        // Same "doctype" tag used on every row this flow inserts into the
        // shared certificate_registration_attachment table, so the pending-
        // approval queries below only ever pick up valid-ID uploads and not
        // whatever other attachment kinds that table may hold.
        private const string ValidIdDocType = "ValidID";

        private static string GuessImageContentType(string? fileName)
        {
            var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };
        }

        [HttpPost("submit_certificate_request")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> SubmitCertificateRequest([FromForm] SubmitCertificateRequestForm form)
        {
            if (form.SignatureImg == null || form.SignatureImg.Length == 0)
                return BadRequest(new { success = false, message = "A main signature specimen is required." });

            // certificate_registration_attachment rows must exist with reg_id
            // = 0 (keyed by reg_eid) *before* GenerateCertificateCoreAsync
            // runs — its SQL batch links them to the new reg_id via
            // `update ... set reg_id = @id where reg_eid = @eid and reg_id = 0`.
            foreach (var file in form.ValidIds)
            {
                if (file.Length == 0) continue;
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                await _dbService.ExecuteAsync(
                    @"INSERT INTO bacpdfsign.dbo.certificate_registration_attachment
                        (reg_id, reg_attachment, reg_file_name, reg_doctype, reg_eid)
                      VALUES (0, @RegAttachment, @RegFileName, @RegDoctype, @RegEid)",
                    new
                    {
                        RegAttachment = Convert.ToBase64String(ms.ToArray()),
                        RegFileName = file.FileName,
                        RegDoctype = ValidIdDocType,
                        RegEid = form.Eid
                    },
                    CommandType.Text
                );
            }

            string signatureBase64;
            using (var ms = new MemoryStream())
            {
                await form.SignatureImg.CopyToAsync(ms);
                signatureBase64 = $"data:{form.SignatureImg.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
            }

            var req = new GenerateCertificateRequest
            {
                FullName = form.FullName,
                Email = form.Email,
                Organisation = form.Organisation,
                Locality = form.Locality,
                Province = form.Province,
                TwoLetter = form.TwoLetter,
                Passwords = form.Passwords,
                CpNumbers = form.CpNumbers,
                DOtp = form.DOtp ?? "",
                PinCode = form.PinCode,
                Eid = form.Eid,
                UserType = form.UserType,
                SignatureBase64 = signatureBase64
            };

            var result = await GenerateCertificateCoreAsync(req);
            if (!result.Success)
            {
                _logger.LogError("Error submitting certificate request for eid: {Eid}, userType: {UserType}: {Error}", form.Eid, form.UserType, result.ErrorMessage);
                return StatusCode((int)HttpStatusCode.InternalServerError, new
                {
                    success = false,
                    message = "An error occurred while submitting the certificate request.",
                    error = result.ErrorMessage
                });
            }

            return Ok(new
            {
                success = true,
                regId = result.RegId,
                message = "Certificate request submitted and pending admin approval."
            });
        }

        // Lets a user fix and resubmit a request an admin returned for
        // revision (user_status = 4, "for edit") — updates the SAME reg_id
        // back to pending instead of creating a new certificate_registration
        // row. Does not touch the PFX itself, since a return is normally
        // about the specimen/valid-ID content, not the certificate.
        [HttpPost("resubmit_certificate_request")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ResubmitCertificateRequest([FromForm] SubmitCertificateRequestForm form)
        {
            if (form.RegId <= 0)
                return BadRequest(new { success = false, message = "RegId is required." });
            if (form.SignatureImg == null || form.SignatureImg.Length == 0)
                return BadRequest(new { success = false, message = "A main signature specimen is required." });

            var owns = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"SELECT COUNT(1) FROM bacpdfsign.dbo.certificate_registration
                  WHERE reg_id = @RegId AND user_eid = @Eid AND user_type = @UserType AND user_status = 4",
                new { form.RegId, form.Eid, form.UserType },
                CommandType.Text
            );
            if (owns == 0)
                return BadRequest(new { success = false, message = "This request cannot be edited — it is not currently returned for revision." });

            // Replace valid IDs entirely rather than appending — the ones
            // flagged by the admin shouldn't linger alongside the new ones.
            await _dbService.ExecuteAsync(
                "DELETE FROM bacpdfsign.dbo.certificate_registration_attachment WHERE reg_id = @RegId AND reg_doctype = @ValidIdDocType",
                new { form.RegId, ValidIdDocType },
                CommandType.Text
            );
            foreach (var file in form.ValidIds)
            {
                if (file.Length == 0) continue;
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                await _dbService.ExecuteAsync(
                    @"INSERT INTO bacpdfsign.dbo.certificate_registration_attachment
                        (reg_id, reg_attachment, reg_file_name, reg_doctype, reg_eid)
                      VALUES (@RegId, @RegAttachment, @RegFileName, @RegDoctype, @RegEid)",
                    new
                    {
                        form.RegId,
                        RegAttachment = Convert.ToBase64String(ms.ToArray()),
                        RegFileName = file.FileName,
                        RegDoctype = ValidIdDocType,
                        RegEid = form.Eid
                    },
                    CommandType.Text
                );
            }

            string signatureBase64;
            using (var ms = new MemoryStream())
            {
                await form.SignatureImg.CopyToAsync(ms);
                signatureBase64 = $"data:{form.SignatureImg.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
            }

            var encryptedPassword = SPMS.Rijndael.Encrypt(form.Passwords);

            await _dbService.ExecuteAsync(
                @"UPDATE bacpdfsign.dbo.certificate_registration
                  SET user_status = 0, user_decided_datetime = NULL, user_fname = @FullName, user_email = @Email,
                      user_organisation = @Organisation, user_locality = @Locality, user_province = @Province,
                      user_twoletter = @TwoLetter, user_password = @Password, user_cpnumber = @CpNumbers, user_pin = @PinCode
                  WHERE reg_id = @RegId;
                  UPDATE bacpdfsign.dbo.pfx_attachments
                  SET passwords = @Password, signatures = @Signatures, pin_code = @PinCode
                  WHERE reg_id = @RegId;",
                new
                {
                    form.RegId,
                    form.FullName,
                    form.Email,
                    form.Organisation,
                    form.Locality,
                    form.Province,
                    form.TwoLetter,
                    Password = encryptedPassword,
                    form.CpNumbers,
                    form.PinCode,
                    Signatures = signatureBase64
                },
                CommandType.Text
            );

            return Ok(new
            {
                success = true,
                regId = form.RegId,
                message = "Certificate request resubmitted and pending admin approval."
            });
        }

        // Registers a certificate the user already has (e.g. PKI-issued
        // elsewhere) instead of having the server generate one via
        // GenerateCertificateCoreAsync/BouncyCastle. Mirrors
        // submit_certificate_request's pending-approval pattern (same
        // certificate_registration/pfx_attachments insert shape, active/
        // status = 0 until an admin approves) but skips BouncyCastle entirely
        // and stores the uploaded .p12 as-is. No OTP field by design.
        [HttpPost("upload_certificate")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadCertificate([FromForm] UploadCertificateForm form)
        {
            if (form.P12File == null || form.P12File.Length == 0)
                return BadRequest(new { success = false, message = "A .p12/.pfx certificate file is required." });
            if (form.SignatureImg == null || form.SignatureImg.Length == 0)
                return BadRequest(new { success = false, message = "A main signature specimen is required." });

            byte[] pfxBytes;
            using (var ms = new MemoryStream())
            {
                await form.P12File.CopyToAsync(ms);
                pfxBytes = ms.ToArray();
            }

            string serialNumberStr;
            try
            {
                using var testCert = new X509Certificate2(pfxBytes, form.Password, X509KeyStorageFlags.EphemeralKeySet);
                serialNumberStr = testCert.SerialNumber;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rejected upload_certificate for eid: {Eid} — certificate did not open with the given password", form.Eid);
                var message = string.IsNullOrEmpty(form.Password)
                    ? "This certificate is password-protected — please enter its password."
                    : "The certificate file could not be opened with the given password.";
                return BadRequest(new { success = false, message });
            }

            string signatureBase64;
            using (var ms = new MemoryStream())
            {
                await form.SignatureImg.CopyToAsync(ms);
                signatureBase64 = $"data:{form.SignatureImg.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
            }

            var networkPath = _fileStorage.RootPath;
            var basePath = DigitalSpicimenPath;
            var userTypeFolder = Path.Combine(basePath, form.UserType);
            var eidFolder = Path.Combine(userTypeFolder, form.Eid);
            var pfxPath = Path.Combine(eidFolder, $"cert_{form.Eid}.p12");
            var pfxBase64Path = Path.Combine(eidFolder, $"cert_{form.Eid}.b64");
            var pngPath = Path.Combine(eidFolder, $"signature_{form.Eid}.png");

            using (_fileStorage.Connect(networkPath))
            {
                await _fileStorage.WriteFileAsync(pfxPath, pfxBytes);
                await _fileStorage.WriteFileAsync(pfxBase64Path, System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(pfxBytes)));

                var cleanBase64 = Regex.Replace(signatureBase64, @"^[\w/\:.-]+;base64,", string.Empty);
                await _fileStorage.WriteFileAsync(pngPath, Convert.FromBase64String(cleanBase64));

                var encryptedPassword = SPMS.Rijndael.Encrypt(form.Password);
                var randomCode = Guid.NewGuid().ToString("N").Substring(0, 10);

                var sql = @"
insert into bacpdfsign.dbo.certificate_registration
    (user_eid, user_type, user_fname, user_email, user_organisation, user_locality, user_province, user_twoletter, user_password, user_datetime, user_status, user_cpnumber, user_otp, user_serial_number, user_pin, reg_source)
values
    (@eid, @user_type, @fullname, @email, @organisation, @locality, @province, @two_letter, @pasw, getdate(), 0, @cp_numbers, '', @serialNumber, @pinCode, 1);
declare @id int = SCOPE_IDENTITY();
insert into bacpdfsign.dbo.pfx_attachments
    (pfx_attachement, passwords, signatures, eid, code, user_type, active, reg_id, pin_code)
values
    (@pfx_attachement, @pasw, @signatures, @eid, @randoms, @user_type, 0, @id, @pinCode);
select @id as reg_id;
";

                try
                {
                    var regId = await _dbService.ExecuteScalarAsync<int, dynamic>(
                        sql,
                        new
                        {
                            pfx_attachement = pfxBytes,
                            eid = form.Eid,
                            user_type = form.UserType,
                            randoms = randomCode,
                            signatures = signatureBase64,
                            pasw = encryptedPassword,
                            fullname = form.FullName,
                            email = form.Email,
                            organisation = form.Organisation,
                            locality = form.Locality,
                            province = form.Province,
                            two_letter = form.TwoLetter,
                            cp_numbers = form.CpNumbers,
                            pinCode = form.PinCode,
                            serialNumber = serialNumberStr
                        },
                        CommandType.Text
                    );

                    return Ok(new
                    {
                        success = true,
                        regId,
                        message = "Certificate uploaded and pending admin approval."
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error uploading certificate for eid: {Eid}, userType: {UserType}", form.Eid, form.UserType);
                    return StatusCode((int)HttpStatusCode.InternalServerError, new
                    {
                        success = false,
                        message = "An error occurred while uploading the certificate.",
                        error = ex.Message
                    });
                }
            }
        }

        // Lets a user fix and resubmit an uploaded certificate an admin
        // returned for revision (user_status = 4, "for edit") — updates the
        // SAME reg_id back to pending. Unlike resubmit_certificate_request,
        // this also replaces the .p12/signature files on disk since
        // re-uploading the certificate itself is a valid reason this flow
        // gets returned.
        [HttpPost("resubmit_uploaded_certificate")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ResubmitUploadedCertificate([FromForm] UploadCertificateForm form)
        {
            if (form.RegId <= 0)
                return BadRequest(new { success = false, message = "RegId is required." });
            if (form.P12File == null || form.P12File.Length == 0)
                return BadRequest(new { success = false, message = "A .p12/.pfx certificate file is required." });
            if (form.SignatureImg == null || form.SignatureImg.Length == 0)
                return BadRequest(new { success = false, message = "A main signature specimen is required." });

            var owns = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"SELECT COUNT(1) FROM bacpdfsign.dbo.certificate_registration
                  WHERE reg_id = @RegId AND user_eid = @Eid AND user_type = @UserType AND user_status = 4",
                new { form.RegId, form.Eid, form.UserType },
                CommandType.Text
            );
            if (owns == 0)
                return BadRequest(new { success = false, message = "This request cannot be edited — it is not currently returned for revision." });

            byte[] pfxBytes;
            using (var ms = new MemoryStream())
            {
                await form.P12File.CopyToAsync(ms);
                pfxBytes = ms.ToArray();
            }

            try
            {
                using var testCert = new X509Certificate2(pfxBytes, form.Password, X509KeyStorageFlags.EphemeralKeySet);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rejected resubmit_uploaded_certificate for eid: {Eid} — certificate did not open with the given password", form.Eid);
                var message = string.IsNullOrEmpty(form.Password)
                    ? "This certificate is password-protected — please enter its password."
                    : "The certificate file could not be opened with the given password.";
                return BadRequest(new { success = false, message });
            }

            string signatureBase64;
            using (var ms = new MemoryStream())
            {
                await form.SignatureImg.CopyToAsync(ms);
                signatureBase64 = $"data:{form.SignatureImg.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
            }

            var networkPath = _fileStorage.RootPath;
            var eidFolder = Path.Combine(DigitalSpicimenPath, form.UserType, form.Eid);
            var pfxPath = Path.Combine(eidFolder, $"cert_{form.Eid}.p12");
            var pfxBase64Path = Path.Combine(eidFolder, $"cert_{form.Eid}.b64");
            var pngPath = Path.Combine(eidFolder, $"signature_{form.Eid}.png");

            using (_fileStorage.Connect(networkPath))
            {
                await _fileStorage.WriteFileAsync(pfxPath, pfxBytes);
                await _fileStorage.WriteFileAsync(pfxBase64Path, System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(pfxBytes)));
                await _fileStorage.DeleteFileAsync(pngPath);
            }

            var encryptedPassword = SPMS.Rijndael.Encrypt(form.Password);

            await _dbService.ExecuteAsync(
                @"UPDATE bacpdfsign.dbo.certificate_registration
                  SET user_status = 0, user_decided_datetime = NULL, user_password = @Password, user_pin = @PinCode
                  WHERE reg_id = @RegId;
                  UPDATE bacpdfsign.dbo.pfx_attachments
                  SET pfx_attachement = @PfxAttachement, passwords = @Password, signatures = @Signatures, pin_code = @PinCode
                  WHERE reg_id = @RegId;",
                new
                {
                    form.RegId,
                    Password = encryptedPassword,
                    form.PinCode,
                    PfxAttachement = pfxBytes,
                    Signatures = signatureBase64
                },
                CommandType.Text
            );

            return Ok(new
            {
                success = true,
                regId = form.RegId,
                message = "Certificate resubmitted and pending admin approval."
            });
        }

        // status: 0 = pending, 1 = approved, 3 = cancelled/discarded,
        // 4 = for edit (returned, editable). 2 is intentionally unused. The
        // same eid/userType can legitimately have several rows here (e.g.
        // re-requesting a certificate after a forgotten password), so this
        // intentionally returns every matching row rather than one per person.
        [HttpGet("list_certificate_requests")]
        public async Task<IActionResult> ListCertificateRequests([FromQuery] int status = 0)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var sql = @"
                SELECT
                    cr.reg_id AS RegId,
                    CAST(cr.user_eid AS NVARCHAR(20)) AS Eid,
                    CAST(cr.user_type AS NVARCHAR(10)) AS UserType,
                    cr.user_fname AS FullName,
                    sn.fname AS SignatoryName,
                    sn.position AS Position,
                    sn.office_name AS OfficeName,
                    cr.user_email AS Email,
                    cr.user_organisation AS Organisation,
                    cr.user_locality AS Locality,
                    cr.user_province AS Province,
                    cr.user_cpnumber AS CpNumbers,
                    cr.user_datetime AS RequestDate,
                    cr.user_decided_datetime AS DecidedDate,
                    cr.user_status AS Status,
                    cr.reg_source AS Source,
                    cr.user_reason AS Remarks,
                    CASE WHEN pa.signatures IS NOT NULL AND pa.signatures <> '' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS HasSignature,
                    (SELECT COUNT(1) FROM bacpdfsign.dbo.certificate_registration_attachment a WHERE a.reg_id = cr.reg_id AND a.reg_doctype = @ValidIdDocType) AS ValidIdCount
                FROM bacpdfsign.dbo.certificate_registration cr
                LEFT JOIN bacpdfsign.dbo.pfx_attachments pa ON pa.reg_id = cr.reg_id
                LEFT JOIN bacpdfsign.dbo.signatory_names sn ON sn.eid = cr.user_eid AND sn.user_type = cr.user_type
                WHERE cr.user_status = @Status
                ORDER BY cr.user_datetime DESC";

            var rows = await _dbService.QueryAsync<PendingCertificateRequestDto, dynamic>(sql, new { ValidIdDocType, Status = status }, CommandType.Text);
            return Ok(rows);
        }

        // Admin view of pfx_attachments directly — the *live* credentials
        // themselves, independent of the certificate_registration request/
        // approval workflow above. A row with reg_id = 0 was never
        // provisioned through this app's own request/upload flow at all
        // (e.g. inserted directly by IT/DICT), labeled "DICT" here; every
        // other row went through submit_certificate_request/upload_certificate
        // and is labeled "PGAS".
        [HttpGet("list_active_pfx_certificates")]
        public async Task<IActionResult> ListActivePfxCertificates([FromQuery] bool includeInactive = false)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var sql = @"
                SELECT
                    pa.id AS Id,
                    pa.eid AS Eid,
                    CAST(pa.user_type AS NVARCHAR(10)) AS UserType,
                    sn.fname AS SignatoryName,
                    sn.position AS Position,
                    sn.office_name AS OfficeName,
                    pa.code AS Code,
                    pa.active AS Active,
                    pa.reg_id AS RegId,
                    CASE WHEN pa.reg_id = 0 THEN 'DICT' ELSE 'PGAS' END AS Source,
                    CASE WHEN ISNULL(LTRIM(RTRIM(pa.passwords)), '') <> '' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS HasPassword
                FROM bacpdfsign.dbo.pfx_attachments pa
                LEFT JOIN bacpdfsign.dbo.signatory_names sn ON sn.eid = pa.eid AND sn.user_type = pa.user_type
                WHERE (@IncludeInactive = 1 OR pa.active = 1)
                ORDER BY pa.active DESC, sn.fname, pa.eid";

            var rows = await _dbService.QueryAsync<ActivePfxCertificateDto, dynamic>(
                sql, new { IncludeInactive = includeInactive }, CommandType.Text);
            return Ok(rows);
        }

        // Changes which pfx_attachments row is "the" active one for a
        // person. Activating one row deactivates every sibling row sharing
        // the same eid+user_type in the same call, so at most one row can
        // ever be active per person — prevents the exact ambiguity that
        // previously caused two rows (different pin_code/active values) to
        // both look like valid candidates for the same eid/user_type.
        [HttpPost("set_pfx_active")]
        public async Task<IActionResult> SetPfxActive([FromBody] SetPfxActiveRequest request)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var target = await _dbService.QueryFirstOrDefaultAsync<dynamic, dynamic>(
                "SELECT eid AS Eid, CAST(user_type AS NVARCHAR(10)) AS UserType FROM bacpdfsign.dbo.pfx_attachments WHERE id = @Id",
                new { request.Id },
                CommandType.Text);

            if (target == null)
                return NotFound(new { success = false, message = "Certificate row not found." });

            var deactivatedSiblings = 0;
            if (request.Active)
            {
                deactivatedSiblings = await _dbService.ExecuteAsync(
                    @"UPDATE bacpdfsign.dbo.pfx_attachments SET active = 0
                      WHERE eid = @Eid AND user_type = @UserType AND id <> @Id AND active = 1",
                    new { Eid = (string)target.Eid, UserType = (string)target.UserType, request.Id },
                    CommandType.Text);
            }

            await _dbService.ExecuteAsync(
                "UPDATE bacpdfsign.dbo.pfx_attachments SET active = @Active WHERE id = @Id",
                new { request.Active, request.Id },
                CommandType.Text);

            return Ok(new { success = true, deactivatedSiblings });
        }

        // Admin-side PIN reset — an operational tool separate from the
        // request/approval workflow, for when a signer forgets their PIN.
        [HttpPost("update_pfx_pin_code")]
        public async Task<IActionResult> UpdatePfxPinCode([FromBody] UpdatePfxPinCodeRequest request)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            if (!Regex.IsMatch(request.NewPinCode ?? "", @"^[0-9a-zA-Z]{5}$"))
                return BadRequest(new { success = false, message = "PIN code must be exactly 5 alphanumeric characters." });

            var affected = await _dbService.ExecuteAsync(
                "UPDATE bacpdfsign.dbo.pfx_attachments SET pin_code = @NewPinCode WHERE id = @Id",
                new { request.NewPinCode, request.Id },
                CommandType.Text);

            if (affected == 0)
                return NotFound(new { success = false, message = "Certificate row not found." });

            return Ok(new { success = true });
        }

        // Admin-side "forget this saved password" — mirrors the legacy
        // VotingApp UploadPFXController's direct `passwords = ''` update,
        // generalized to any row by id instead of a hardcoded eid. Once
        // cleared, PIN-based signing for this row correctly falls back to
        // asking the signer for their password directly (nothing left to
        // decrypt) instead of silently failing.
        [HttpPost("remove_pfx_password")]
        public async Task<IActionResult> RemovePfxPassword([FromBody] RemovePfxPasswordRequest request)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var affected = await _dbService.ExecuteAsync(
                "UPDATE bacpdfsign.dbo.pfx_attachments SET passwords = '' WHERE id = @Id",
                new { request.Id },
                CommandType.Text);

            if (affected == 0)
                return NotFound(new { success = false, message = "Certificate row not found." });

            return Ok(new { success = true });
        }

        // Full history for one person across both certificate flows (Request
        // and Upload) plus every Change Specimen action — shown on the
        // review dialog so an admin isn't only looking at the single request
        // in front of them.
        [HttpGet("certificate_request_user_history")]
        public async Task<IActionResult> GetCertificateRequestUserHistory([FromQuery] string eid, [FromQuery] string userType)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var requests = await _dbService.QueryAsync<CertificateHistoryEntryDto, dynamic>(
                @"SELECT
                      cr.reg_id AS RegId,
                      cr.user_datetime AS RequestDate,
                      cr.user_decided_datetime AS DecidedDate,
                      cr.user_status AS Status,
                      cr.reg_source AS Source,
                      cr.user_reason AS Remarks
                  FROM bacpdfsign.dbo.certificate_registration cr
                  WHERE cr.user_eid = @Eid AND cr.user_type = @UserType
                  ORDER BY cr.user_datetime DESC",
                new { Eid = eid, UserType = userType },
                CommandType.Text
            );

            var specimenChanges = await _dbService.QueryAsync<SpecimenHistoryEntryDto, dynamic>(
                @"SELECT specimen_type AS SpecimenType, changed_at AS ChangedAt
                  FROM bacpdfsign.dbo.signature_specimen_history
                  WHERE eid = @Eid AND user_type = @UserType
                  ORDER BY changed_at DESC",
                new { Eid = eid, UserType = userType },
                CommandType.Text
            );

            return Ok(new { requests, specimenChanges });
        }

        [HttpGet("certificate_request_valid_ids")]
        public async Task<IActionResult> GetCertificateRequestValidIds([FromQuery] int regId)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var rows = await _dbService.QueryAsync<CertificateValidIdDto, dynamic>(
                @"SELECT id AS Id, reg_file_name AS FileName
                  FROM bacpdfsign.dbo.certificate_registration_attachment
                  WHERE reg_id = @RegId AND reg_doctype = @ValidIdDocType
                  ORDER BY id",
                new { RegId = regId, ValidIdDocType },
                CommandType.Text
            );
            return Ok(rows);
        }

        [HttpGet("certificate_request_valid_id_image")]
        public async Task<IActionResult> GetCertificateRequestValidIdImage([FromQuery] int id)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var row = await _dbService.QueryFirstOrDefaultAsync<dynamic, dynamic>(
                @"SELECT reg_attachment AS RegAttachment, reg_file_name AS RegFileName
                  FROM bacpdfsign.dbo.certificate_registration_attachment
                  WHERE id = @Id AND reg_doctype = @ValidIdDocType",
                new { Id = id, ValidIdDocType },
                CommandType.Text
            );
            if (row == null) return NotFound();

            string base64 = row.RegAttachment?.ToString() ?? "";
            if (string.IsNullOrEmpty(base64)) return NotFound();

            var cleanBase64 = Regex.Replace(base64, @"^[\w/\:.-]+;base64,", string.Empty);
            byte[] bytes = Convert.FromBase64String(cleanBase64);
            string contentType = GuessImageContentType(row.RegFileName?.ToString());
            return File(bytes, contentType);
        }

        // get_signature_image_merged only ever reads the ACTIVE pfx_attachments
        // row (WHERE active = 1), so it can't show what's actually attached to
        // a still-pending request — either nothing (first-time requester) or,
        // worse, a stale already-approved signature (re-requester). This reads
        // the row by reg_id directly, pending or not, so review always shows
        // exactly what was submitted with that specific request.
        [HttpGet("certificate_request_signature_image")]
        public async Task<IActionResult> GetCertificateRequestSignatureImage([FromQuery] int regId)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var row = await _dbService.QueryFirstOrDefaultAsync<dynamic, dynamic>(
                @"SELECT signatures AS Signatures
                  FROM bacpdfsign.dbo.pfx_attachments
                  WHERE reg_id = @RegId",
                new { RegId = regId },
                CommandType.Text
            );
            if (row == null) return NotFound();

            string base64 = row.Signatures?.ToString() ?? "";
            if (string.IsNullOrEmpty(base64)) return NotFound();

            var cleanBase64 = Regex.Replace(base64, @"^[\w/\:.-]+;base64,", string.Empty);
            byte[] bytes = Convert.FromBase64String(cleanBase64);
            return File(bytes, "image/png");
        }

        [HttpPost("approve_certificate_request")]
        public async Task<IActionResult> ApproveCertificateRequest([FromBody] CertificateRequestActionRequest req)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            await _dbService.ExecuteAsync(
                @"UPDATE bacpdfsign.dbo.certificate_registration SET user_status = 1, user_decided_datetime = GETDATE() WHERE reg_id = @RegId;
                  UPDATE bacpdfsign.dbo.pfx_attachments SET active = 1 WHERE reg_id = @RegId;",
                new { req.RegId },
                CommandType.Text
            );
            return Ok(new { success = true });
        }

        // user_status = 3: cancelled/discarded — a final negative outcome
        // (route/endpoint name kept as "reject" for backward compatibility
        // with existing callers; only the stored status value changed).
        [HttpPost("reject_certificate_request")]
        public async Task<IActionResult> RejectCertificateRequest([FromBody] CertificateRequestActionRequest req)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            await _dbService.ExecuteAsync(
                @"UPDATE bacpdfsign.dbo.certificate_registration
                  SET user_status = 3, user_decided_datetime = GETDATE(), user_reason = @Remarks
                  WHERE reg_id = @RegId",
                new { req.RegId, Remarks = req.Remarks ?? "" },
                CommandType.Text
            );
            return Ok(new { success = true });
        }

        // user_status = 4 ("for edit"): distinct from cancel/discard — the
        // request stays tied to the same reg_id and the user can edit +
        // resubmit it (e.g. "please reupload a valid ID without a
        // background"), rather than starting an entirely new request.
        [HttpPost("return_certificate_request")]
        public async Task<IActionResult> ReturnCertificateRequest([FromBody] CertificateRequestActionRequest req)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            if (string.IsNullOrWhiteSpace(req.Remarks))
                return BadRequest(new { success = false, message = "Remarks are required when returning a request for revision." });

            await _dbService.ExecuteAsync(
                @"UPDATE bacpdfsign.dbo.certificate_registration
                  SET user_status = 4, user_decided_datetime = GETDATE(), user_reason = @Remarks
                  WHERE reg_id = @RegId",
                new { req.RegId, Remarks = req.Remarks },
                CommandType.Text
            );
            return Ok(new { success = true });
        }

        [HttpGet("get_signatory_by_sigid")]
        public async Task<IActionResult> GetSignatoryBySigId([FromQuery] long sigId)
        {
            try
            {
                var signatory = await _signingService.GetSignatoryBySigIdAsync(sigId);

                if (signatory == null)
                    return NotFound(new { success = false, message = "Signatory not found" });

                return Ok(signatory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting signatory for sigId: {SigId}", sigId);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while fetching signatory", error = ex.Message });
            }
        }


        [HttpGet("get_pfx_attachments_by_eid")]
        public async Task<IActionResult> GetPfxAttachmentsByEid(
            [FromQuery] int eid,
            [FromQuery] int userType = 0)
        {
            try
            {
                var hasPassword = await _signingService.GetPfxAttachmentsByEidAsync(eid, userType);
                return Ok(hasPassword);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking pfx password presence for eid: {Eid}, userType: {UserType}", eid, userType);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while checking pfx password presence", error = ex.Message });
            }
        }

        [HttpGet("get_pincode_by_eid")]
        public async Task<IActionResult> GetPincodeByEid(
            [FromQuery] int eid,
            [FromQuery] int userType = 0,
            [FromQuery] string pincode = "")
        {
            try
            {
                var match = await _signingService.GetPincodeByEidAsync(eid, userType, pincode);
                return Ok(match);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking pincode for eid: {Eid}, userType: {UserType}", eid, userType);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while checking pincode", error = ex.Message });
            }
        }


        [HttpGet("get_checkpassword_by_eid")]
        public async Task<IActionResult> GetCheckpassword(
      [FromQuery] int eid,
      [FromQuery] int userType = 0,
      [FromQuery] string password = "")
        {
            try
            {
                var result = await _signingService.GetCheckPasswordByEidAsync(eid, userType, password);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking certificate password for eid: {Eid}, userType: {UserType}", eid, userType);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "An error occurred while checking the certificate password", error = ex.Message });
            }
        }


    }
}