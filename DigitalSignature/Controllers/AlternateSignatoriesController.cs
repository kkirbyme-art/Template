using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Net;
using System.Security.Claims;
using DigitalSignature.Models;
using DigitalSignature.Services;

namespace DigitalSignature.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AlternateSignatoriesController : Controller
    {
        private readonly IDatabaseService _dbService;
        private readonly ISigningService _signingService;
        private readonly ILogger<AlternateSignatoriesController> _logger;

        public AlternateSignatoriesController(
            IDatabaseService dbService,
            ISigningService signingService,
            ILogger<AlternateSignatoriesController> logger)
        {
            _dbService = dbService;
            _signingService = signingService;
            _logger = logger;
        }

        // Single source of truth for "is this alternate_signatories row currently
        // active" — used by both my_principals and IsActiveAlternateForAsync so
        // they can never drift apart. Assumes the table is aliased "a".
        private const string ActiveAlternateCondition =
            "a.isactive = 1 AND (a.is_permanent = 1 OR (CAST(GETDATE() AS DATE) BETWEEN a.dateFrom AND a.dateTo))";

        // Caller's own identity from JWT claims — never trust a client-supplied
        // "owner" eid/userType for anything that reads or mutates someone's own
        // alternate list (mirrors DGSignController.MyCertificateRequests).
        private (int Eid, int UserType)? GetCurrentUser()
        {
            var eidClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userTypeClaim = User.FindFirstValue("UserType");
            if (!int.TryParse(eidClaim, out var eid) || !int.TryParse(userTypeClaim, out var userType))
                return null;
            return (eid, userType);
        }

        [HttpGet("my_alternates")]
        public async Task<IActionResult> GetMyAlternates()
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            var results = await _dbService.QueryAsync<AlternateSignatoryDto, dynamic>(
                @"SELECT
                      a.id AS Id,
                      a.user_alternate_eid AS AlternateEid,
                      a.user_alternate_type AS AlternateUserType,
                      sn.fname AS AlternateName,
                      sn.office_name AS AlternateOffice,
                      ISNULL(a.isactive, 0) AS IsActive,
                      ISNULL(a.is_permanent, 0) AS IsPermanent,
                      a.dateFrom AS DateFrom,
                      a.dateTo AS DateTo
                  FROM bacpdfsign.dbo.alternate_signatories a
                  LEFT JOIN bacpdfsign.dbo.signatory_names sn
                      ON sn.eid = a.user_alternate_eid AND sn.user_type = a.user_alternate_type
                  WHERE a.user_eid = @Eid AND a.user_type = @UserType
                  ORDER BY sn.fname",
                new { me.Value.Eid, me.Value.UserType },
                CommandType.Text);

            return Ok(results);
        }

        [HttpPost("add_alternate")]
        public async Task<IActionResult> AddAlternate([FromBody] AddAlternateRequest request)
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            if (request.AlternateEid == me.Value.Eid && request.AlternateUserType == me.Value.UserType)
                return BadRequest(new { success = false, message = "You cannot set yourself as your own alternate." });

            if (!request.IsPermanent)
            {
                if (request.DateFrom is null || request.DateTo is null)
                    return BadRequest(new { success = false, message = "Both a start and end date are required unless this alternate is permanent." });
                if (request.DateFrom > request.DateTo)
                    return BadRequest(new { success = false, message = "The start date must not be after the end date." });
            }

            var existing = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"SELECT COUNT(1) FROM bacpdfsign.dbo.alternate_signatories
                  WHERE user_eid = @Eid AND user_type = @UserType
                    AND user_alternate_eid = @AlternateEid AND user_alternate_type = @AlternateUserType",
                new { me.Value.Eid, me.Value.UserType, request.AlternateEid, request.AlternateUserType },
                CommandType.Text);
            if (existing > 0)
                return BadRequest(new { success = false, message = "This person is already one of your alternates." });

            var newId = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"INSERT INTO bacpdfsign.dbo.alternate_signatories
                      (user_eid, user_type, user_alternate_eid, user_alternate_type, isactive, is_permanent, dateFrom, dateTo)
                  OUTPUT INSERTED.id
                  VALUES
                      (@Eid, @UserType, @AlternateEid, @AlternateUserType, 1, @IsPermanent, @DateFrom, @DateTo)",
                new
                {
                    me.Value.Eid,
                    me.Value.UserType,
                    request.AlternateEid,
                    request.AlternateUserType,
                    request.IsPermanent,
                    DateFrom = request.IsPermanent ? null : request.DateFrom,
                    DateTo = request.IsPermanent ? null : request.DateTo,
                },
                CommandType.Text);

            return Ok(new { success = true, id = newId });
        }

        [HttpPut("update_alternate/{id:int}")]
        public async Task<IActionResult> UpdateAlternate(int id, [FromBody] UpdateAlternateRequest request)
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            if (!request.IsPermanent)
            {
                if (request.DateFrom is null || request.DateTo is null)
                    return BadRequest(new { success = false, message = "Both a start and end date are required unless this alternate is permanent." });
                if (request.DateFrom > request.DateTo)
                    return BadRequest(new { success = false, message = "The start date must not be after the end date." });
            }

            var affected = await _dbService.ExecuteAsync(
                @"UPDATE bacpdfsign.dbo.alternate_signatories
                  SET is_permanent = @IsPermanent, dateFrom = @DateFrom, dateTo = @DateTo
                  WHERE id = @Id AND user_eid = @Eid AND user_type = @UserType",
                new
                {
                    Id = id,
                    me.Value.Eid,
                    me.Value.UserType,
                    request.IsPermanent,
                    DateFrom = request.IsPermanent ? null : request.DateFrom,
                    DateTo = request.IsPermanent ? null : request.DateTo,
                },
                CommandType.Text);

            if (affected == 0) return NotFound(new { success = false, message = "Alternate not found." });
            return Ok(new { success = true });
        }

        [HttpDelete("delete_alternate/{id:int}")]
        public async Task<IActionResult> DeleteAlternate(int id)
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            var owned = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"SELECT COUNT(1) FROM bacpdfsign.dbo.alternate_signatories
                  WHERE id = @Id AND user_eid = @Eid AND user_type = @UserType",
                new { Id = id, me.Value.Eid, me.Value.UserType },
                CommandType.Text);
            if (owned == 0) return NotFound(new { success = false, message = "Alternate not found." });

            // No DB-level FK exists between the two tables (both predate this
            // feature) — clean up the child document-type rows first, then the
            // parent row, inside one transaction so a failure never leaves an
            // orphaned document-type row behind.
            var tx = await _dbService.BeginTransactionAsync();
            try
            {
                await _dbService.ExecuteAsync(
                    "DELETE FROM bacpdfsign.dbo.alternate_signatories_documents WHERE alter_id = @Id",
                    new { Id = id }, CommandType.Text, tx);

                await _dbService.ExecuteAsync(
                    "DELETE FROM bacpdfsign.dbo.alternate_signatories WHERE id = @Id",
                    new { Id = id }, CommandType.Text, tx);

                await _dbService.CommitTransactionAsync(tx);
            }
            catch (Exception ex)
            {
                await _dbService.RollbackTransactionAsync(tx);
                _logger.LogError(ex, "Failed to delete alternate {Id}", id);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { success = false, message = "Failed to delete alternate." });
            }

            return Ok(new { success = true });
        }

        [HttpGet("alternate_document_types/{alterId:int}")]
        public async Task<IActionResult> GetAlternateDocumentTypes(int alterId)
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            var owned = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"SELECT COUNT(1) FROM bacpdfsign.dbo.alternate_signatories
                  WHERE id = @AlterId AND user_eid = @Eid AND user_type = @UserType",
                new { AlterId = alterId, me.Value.Eid, me.Value.UserType },
                CommandType.Text);
            if (owned == 0) return NotFound();

            var results = await _dbService.QueryAsync<AlternateDocumentTypeDto, dynamic>(
                @"SELECT
                      asd.id AS Id,
                      asd.doc_type_id AS DocTypeId,
                      dt.document_description AS DocumentDescription,
                      dt.document_abbr AS DocumentAbbr
                  FROM bacpdfsign.dbo.alternate_signatories_documents asd
                  INNER JOIN bacpdfsign.dbo.document_types dt ON dt.id = asd.doc_type_id
                  WHERE asd.alter_id = @AlterId
                  ORDER BY dt.document_description",
                new { AlterId = alterId },
                CommandType.Text);

            return Ok(results);
        }

        [HttpPost("add_document_type")]
        public async Task<IActionResult> AddDocumentType([FromBody] AddAlternateDocumentTypeRequest request)
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            var owned = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"SELECT COUNT(1) FROM bacpdfsign.dbo.alternate_signatories
                  WHERE id = @AlterId AND user_eid = @Eid AND user_type = @UserType",
                new { request.AlterId, me.Value.Eid, me.Value.UserType },
                CommandType.Text);
            if (owned == 0) return NotFound(new { success = false, message = "Alternate not found." });

            var already = await _dbService.ExecuteScalarAsync<int, dynamic>(
                @"SELECT COUNT(1) FROM bacpdfsign.dbo.alternate_signatories_documents
                  WHERE alter_id = @AlterId AND doc_type_id = @DocTypeId",
                new { request.AlterId, request.DocTypeId },
                CommandType.Text);
            if (already > 0) return Ok(new { success = true }); // idempotent add

            await _dbService.ExecuteAsync(
                @"INSERT INTO bacpdfsign.dbo.alternate_signatories_documents (alter_id, doc_type_id, datentime)
                  VALUES (@AlterId, @DocTypeId, GETDATE())",
                new { request.AlterId, request.DocTypeId },
                CommandType.Text);

            return Ok(new { success = true });
        }

        [HttpDelete("delete_document_type/{id:int}")]
        public async Task<IActionResult> DeleteDocumentType(int id)
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            var affected = await _dbService.ExecuteAsync(
                @"DELETE asd FROM bacpdfsign.dbo.alternate_signatories_documents asd
                  INNER JOIN bacpdfsign.dbo.alternate_signatories a ON a.id = asd.alter_id
                  WHERE asd.id = @Id AND a.user_eid = @Eid AND a.user_type = @UserType",
                new { Id = id, me.Value.Eid, me.Value.UserType },
                CommandType.Text);

            if (affected == 0) return NotFound(new { success = false, message = "Document type assignment not found." });
            return Ok(new { success = true });
        }

        [HttpGet("my_principals")]
        public async Task<IActionResult> GetMyPrincipals()
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            var results = await _dbService.QueryAsync<AlternatePrincipalDto, dynamic>(
                $@"SELECT
                      a.user_eid AS PrincipalEid,
                      a.user_type AS PrincipalUserType,
                      sn.fname AS PrincipalName,
                      sn.office_name AS PrincipalOffice
                  FROM bacpdfsign.dbo.alternate_signatories a
                  LEFT JOIN bacpdfsign.dbo.signatory_names sn
                      ON sn.eid = a.user_eid AND sn.user_type = a.user_type
                  WHERE a.user_alternate_eid = @Eid AND a.user_alternate_type = @UserType
                    AND {ActiveAlternateCondition}
                  ORDER BY sn.fname",
                new { me.Value.Eid, me.Value.UserType },
                CommandType.Text);

            return Ok(results);
        }

        // Shared predicate for "is the caller currently an active alternate for
        // this principal" — used by both endpoints below so their authorization
        // check can never drift from what my_principals lists.
        private async Task<bool> IsActiveAlternateForAsync(int principalEid, int principalUserType, int alternateEid, int alternateUserType)
        {
            var count = await _dbService.ExecuteScalarAsync<int, dynamic>(
                $@"SELECT COUNT(1) FROM bacpdfsign.dbo.alternate_signatories a
                  WHERE a.user_eid = @PrincipalEid AND a.user_type = @PrincipalUserType
                    AND a.user_alternate_eid = @AlternateEid AND a.user_alternate_type = @AlternateUserType
                    AND {ActiveAlternateCondition}",
                new { PrincipalEid = principalEid, PrincipalUserType = principalUserType, AlternateEid = alternateEid, AlternateUserType = alternateUserType },
                CommandType.Text);
            return count > 0;
        }

        private async Task<List<int>> GetAllowedDocTypeIdsAsync(int principalEid, int principalUserType, int alternateEid, int alternateUserType)
        {
            var ids = await _dbService.QueryAsync<int, dynamic>(
                $@"SELECT asd.doc_type_id
                  FROM bacpdfsign.dbo.alternate_signatories_documents asd
                  INNER JOIN bacpdfsign.dbo.alternate_signatories a ON a.id = asd.alter_id
                  WHERE a.user_eid = @PrincipalEid AND a.user_type = @PrincipalUserType
                    AND a.user_alternate_eid = @AlternateEid AND a.user_alternate_type = @AlternateUserType
                    AND {ActiveAlternateCondition}",
                new { PrincipalEid = principalEid, PrincipalUserType = principalUserType, AlternateEid = alternateEid, AlternateUserType = alternateUserType },
                CommandType.Text);
            return ids.ToList();
        }

        [HttpGet("pending_document_types_as_alternate")]
        public async Task<IActionResult> GetPendingDocumentTypesAsAlternate(
            [FromQuery] int year, [FromQuery] int principalEid, [FromQuery] int principalUserType)
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            if (!await IsActiveAlternateForAsync(principalEid, principalUserType, me.Value.Eid, me.Value.UserType))
                return Forbid();

            var allowedDocTypeIds = await GetAllowedDocTypeIdsAsync(principalEid, principalUserType, me.Value.Eid, me.Value.UserType);
            if (allowedDocTypeIds.Count == 0) return Ok(new List<PendingDocumentTypeDto>());

            var results = await _signingService.GetPendingDocumentTypesAsync(year, principalEid, principalUserType, allowedDocTypeIds);
            return Ok(results);
        }

        [HttpGet("pending_documents_as_alternate")]
        public async Task<IActionResult> GetPendingDocumentsAsAlternate(
            [FromQuery] int year, [FromQuery] int principalEid, [FromQuery] int principalUserType, [FromQuery] int? docTypeId = null)
        {
            var me = GetCurrentUser();
            if (me is null) return Unauthorized();

            if (!await IsActiveAlternateForAsync(principalEid, principalUserType, me.Value.Eid, me.Value.UserType))
                return Forbid();

            var allowedDocTypeIds = await GetAllowedDocTypeIdsAsync(principalEid, principalUserType, me.Value.Eid, me.Value.UserType);
            if (docTypeId.HasValue && !allowedDocTypeIds.Contains(docTypeId.Value))
                return Forbid();

            var all = await _signingService.GetPendingDocumentsAsync(year, principalEid, principalUserType, docTypeId);
            var filtered = all.Where(d => allowedDocTypeIds.Contains(d.DocTypeId)).ToList();
            return Ok(filtered);
        }
    }
}
