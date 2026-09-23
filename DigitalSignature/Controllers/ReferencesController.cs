using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Net;
using DigitalSignature.Services;

namespace DigitalSignature.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [AllowAnonymous]
    public class ReferencesController : Controller
    {
        private readonly IDatabaseService _dbService;
        private readonly ILogger<ReferencesController> _logger;

        public ReferencesController(IDatabaseService dbService, ILogger<ReferencesController> logger)
        {
            _dbService = dbService;
            _logger = logger;
        }

        [HttpGet("get_document_types")]
        public async Task<IActionResult> GetDocumentTypes()
        {
            try
            {
                var sql = @"
                    SELECT TOP (1000) id AS Id,
                                     document_description AS DocumentDescription,
                                     document_abbr AS DocumentAbbr,
                                     isbulk AS IsBulk
                    FROM [bacpdfsign].[dbo].[document_types]";

                var results = await _dbService.QueryAsync<DocumentTypeDto, dynamic>(
                    sql,
                    new { },
                    CommandType.Text);

                // Transform to standardized DynamicSelectDto format
                var standardizedResults = results.Select(r => new DynamicSelectDto
                {
                    Id = r.Id.ToString(),
                    Value = r.DocumentDescription,
                    Abbr_Value = r.DocumentAbbr,
                    Additional_Id = r.IsBulk.ToString()
                }).ToList();

                return Ok(standardizedResults);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting document types");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching document types", error = ex.Message });
            }
        }

        [HttpGet("get_offices")]
        public async Task<IActionResult> GetOffices()
        {
            try
            {

                var sql = @"SELECT OfficeID AS Id
                              ,OfficeName AS Value
                              ,OfficeAbbr as Abbr_Value
                          FROM bacpdfsign.dbo.signatory_office";

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


        [HttpGet("get_signatory_levels")]
        public async Task<IActionResult> GetSignatoryLevels()
        {
            try
            {
                var sql = @"
                            SELECT TOP (1000) [id] AS Id, [description] AS Description
                            FROM [bacpdfsign].[dbo].[signatory_leveltype]
                            WHERE id != 2";

                var results = (await _dbService.QueryAsync<SignatoryLevelDto, dynamic>(
                    sql,
                    new { },
                    CommandType.Text)).ToList();

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting signatory levels");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching signatory levels", error = ex.Message });
            }
        }

        // Backs the admin document-search edit dialog's status dropdown —
        // read live from req_status rather than hardcoding labels, since
        // doc_status_id has more values in use than are branched on
        // anywhere in this codebase (see AdminSearchDocuments).
        [HttpGet("get_doc_statuses")]
        public async Task<IActionResult> GetDocStatuses()
        {
            try
            {
                var sql = @"SELECT id AS Id, status_type AS StatusType FROM [bacpdfsign].[dbo].[req_status] ORDER BY id";

                var results = (await _dbService.QueryAsync<DocStatusDto, dynamic>(
                    sql,
                    new { },
                    CommandType.Text)).ToList();

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting document statuses");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching document statuses", error = ex.Message });
            }
        }

        // Backs the admin document-search edit dialog's per-signatory status
        // dropdown (document_signatories.sig_status) — same shape/pattern as
        // get_doc_statuses above, just a different lookup table.
        [HttpGet("get_signatory_statuses")]
        public async Task<IActionResult> GetSignatoryStatuses()
        {
            try
            {
                var sql = @"SELECT id AS Id, status_type AS StatusType FROM [bacpdfsign].[dbo].[req_sign_status] ORDER BY id";

                var results = (await _dbService.QueryAsync<DocStatusDto, dynamic>(
                    sql,
                    new { },
                    CommandType.Text)).ToList();

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting signatory statuses");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching signatory statuses", error = ex.Message });
            }
        }

        // Gates the strict signature-count enforcement in pdf_viewer.tsx:
        // if this document's (doc_is, doc_type_id) combo appears in
        // document_type_restricted, adding more signatures than the
        // signatory's assigned sig_sign_count is blocked outright (for
        // non-VIP signers); otherwise it's only ever a soft warning.
        [HttpGet("get_signature_restriction")]
        public async Task<IActionResult> GetSignatureRestriction([FromQuery] int docId)
        {
            try
            {
                var isRestricted = await _dbService.ExecuteScalarAsync<int, dynamic>(
                    @"SELECT CASE WHEN EXISTS (
                          SELECT 1
                          FROM [bacpdfsign].[dbo].[document_type_restricted] dtr
                          INNER JOIN [bacpdfsign].[dbo].[document_attach] da ON da.doc_id = @DocId
                          WHERE dtr.doc_is = da.doc_is AND dtr.doc_type_id = da.doc_type_id
                      ) THEN 1 ELSE 0 END",
                    new { DocId = docId },
                    CommandType.Text);

                return Ok(new { isRestricted = isRestricted > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking signature restriction for doc {DocId}", docId);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while checking signature restriction", error = ex.Message });
            }
        }

        // Gates the VVIP-only signing features in pdf_viewer.tsx (the manual
        // Authority Level override and "Hide Signature Date") — membership is
        // whatever's in vip_no_date, not a role/claim. Everyone else signs
        // with whatever sig_level the document was uploaded with and the
        // date always shown.
        [HttpGet("check_vip")]
        public async Task<IActionResult> CheckVip([FromQuery] int eid, [FromQuery] int userType)
        {
            try
            {
                var count = await _dbService.ExecuteScalarAsync<int, dynamic>(
                    @"SELECT COUNT(1) FROM [bacpdfsign].[dbo].[vip_no_date]
                      WHERE eid = @Eid AND user_type = @UserType",
                    new { Eid = eid, UserType = userType },
                    CommandType.Text);

                return Ok(new { isVip = count > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking VIP status for eid {Eid}", eid);
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while checking VIP status", error = ex.Message });
            }
        }

        [HttpGet("get_listofSignatories")]
        public async Task<IActionResult> GetlistofSignatories()
        {
            try
            {
                var sql = @"
                            SELECT
                                ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Id,
                                fname as Value,
                                office_name as Abbr_Value,
                                eid as Additional_Id,
                                user_type as Additional_Id_two
                            FROM bacpdfsign.dbo.signatory_names order by fname;
                            ";

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

        // Employees scoped to a single office — used by admin document
        // search to let an admin narrow by a specific person once an
        // office is picked, without loading every employee system-wide.
        [HttpGet("get_employees_by_office")]
        public async Task<IActionResult> GetEmployeesByOffice([FromQuery] string officeId)
        {
            if (string.IsNullOrWhiteSpace(officeId))
                return Ok(new List<DynamicSelectDto>());

            try
            {
                var sql = @"
                            SELECT DISTINCT
                                CAST(eid AS NVARCHAR(20)) AS Id,
                                fname AS Value,
                                office_name AS Abbr_Value,
                                CAST(eid AS NVARCHAR(20)) AS Additional_Id,
                                CAST(user_type AS NVARCHAR(20)) AS Additional_Id_two
                            FROM bacpdfsign.dbo.signatory_names
                            WHERE offices = @OfficeId
                            ORDER BY fname;
                            ";

                var results = (await _dbService.QueryAsync<DynamicSelectDto, dynamic>(
                    sql,
                    new { OfficeId = officeId },
                    CommandType.Text)).ToList();

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting employees by office");
                return StatusCode((int)HttpStatusCode.InternalServerError,
                    new { message = "An error occurred while fetching employees", error = ex.Message });
            }
        }
    }

    public class DocumentTypeDto
    {
        public int Id { get; set; }
        public string? DocumentDescription { get; set; }
        public string? DocumentAbbr { get; set; }
        public int IsBulk { get; set; }
    }
    public class SignatoryLevelDto
    {
        public int Id { get; set; }
        public string? Description { get; set; }
    }
    public class DocStatusDto
    {
        public int Id { get; set; }
        public string? StatusType { get; set; }
    }
    public class DynamicSelectDto
    {
        /// <summary>Primary identifier</summary>
        public string Id { get; set; } = "0";

        /// <summary>Display value shown in the dropdown</summary>
        public string Value { get; set; } = "";

        /// <summary>Abbreviated value or secondary info (optional)</summary>
        public string? Abbr_Value { get; set; } = "";

        /// <summary>Additional ID for related data (e.g., flag, category, parent ID)</summary>
        public string? Additional_Id { get; set; } = "0";

        public string? Additional_Id_two { get; set; } = "0";
    }
}
