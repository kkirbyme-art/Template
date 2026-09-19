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
                      a.isactive AS IsActive,
                      a.is_permanent AS IsPermanent,
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
    }
}
