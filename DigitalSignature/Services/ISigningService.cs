using Microsoft.AspNetCore.Http;
using DigitalSignature.Models;

namespace DigitalSignature.Services;

// Extracted from DGSignController.cs so both the authenticated DGSignController
// and the token-authenticated PublicSignController can share the exact same
// document/signing logic. Methods return plain result objects (not
// IActionResult) — each controller maps them to its own HTTP response shape.
public interface ISigningService
{
    Task<List<PendingDocumentDto>> GetPendingDocumentsAsync(int year, int eid, int userType, int? docTypeId);

    Task<IEnumerable<PendingDocumentTypeDto>> GetPendingDocumentTypesAsync(int year, int eid, int userType, IEnumerable<int>? allowedDocTypeIds = null);

    Task<DocumentViewResult?> GetDocumentViewAsync(int docId);

    Task<SignatureImageResult> GetSignatureImageMergedAsync(string eids, string usertypes, string type);

    Task<DocumentSignatoryDto?> GetSignatoryBySigIdAsync(long sigId);

    Task<PdfDigitalOnlyResult> GetPdfDigitalOnlyAsync(int formId);

    Task<bool> GetPfxAttachmentsByEidAsync(int eid, int userType);

    Task<bool> GetPincodeByEidAsync(int eid, int userType, string pincode);

    Task<int> GetCheckPasswordByEidAsync(int eid, int userType, string password);

    Task<SaveSignatureResult> SaveSignatureImageAsync(SaveSignatureRequest request);

    // ── New methods (not part of the original 10) — used by the public-sign
    // flow's ownership checks (see PublicSignController). ──

    // Fetches one PENDING document's full info by id directly for a specific
    // (eid, userType) signatory, without get_pending_documents's @Year filter
    // (awkward for a scope=sign deep link that doesn't know the doc's year).
    // Returns null if no such pending signatory row exists — doubling as the
    // ownership check for the direct-sign flow.
    Task<PendingDocumentDto?> GetPendingDocumentByIdAsync(int docId, int eid, int userType);

    // Cheap existence check: is (eid, userType) a signatory (any status) on docId?
    Task<bool> IsSignatoryOnDocumentAsync(int docId, int eid, int userType);

    // Creates a new document_attach row (+ its document_signatories and
    // optional document_support rows) in one transaction. If pdfFile is
    // supplied, the bytes are saved to NAS at the standard
    // digital_signature\{yy}\Form{docId}\file.pdf location; otherwise the
    // caller must supply DocDirectory/DocName in meta (file already placed
    // on NAS out-of-band) and no physical write happens. See DocumentIntakeController
    // / DGSignController.UploadDocument for the two callers.
    Task<SaveDocumentResult> SaveDocumentAsync(
        UploadDocumentMetaDto meta,
        IFormFile? pdfFile,
        List<IFormFile>? supportingFiles);

    // Wraps GetDocumentViewAsync (unchanged) plus a document_support lookup
    // and the CanEditMain computation, for the Edit dialog's prefill fetch.
    Task<DocumentEditViewResult?> GetDocumentForEditAsync(int docId);

    // Rejects (IsLocked) if any existing signatory has already signed.
    // Otherwise updates document_attach's editable fields, replaces
    // document_signatories wholesale, and optionally replaces the PDF.
    Task<UpdateDocumentResult> UpdateDocumentAsync(
        int docId,
        UploadDocumentMetaDto meta,
        IFormFile? pdfFile);

    // Cascading delete: document_support rows + files, document_signatories
    // rows, the document_attach row, then a best-effort physical folder
    // delete. No status gate — allowed at any time.
    Task<DeleteDocumentResult> DeleteDocumentAsync(int docId);

    // Admin-only override edit — no signed-signatory lock (unlike
    // UpdateDocumentAsync). Updates document_attach fields that
    // UpdateDocumentAsync never touches (doc_code, doc_status_id) plus the
    // usual description/type, and optionally replaces the PDF at the
    // document's existing directory/name. Never touches document_signatories.
    Task<AdminUpdateDocumentResult> AdminUpdateDocumentAsync(AdminUpdateDocumentRequest request, IFormFile? pdfFile);

    // Per-row diff against the submitted list — updates/deletes/inserts as
    // needed, any status (signed rows are no longer protected). Every
    // touched row that was already signed gets an admin_signatory_audit
    // snapshot (including its document_signature_location rows) before
    // being changed.
    Task<AdminUpdateSignatoriesResult> AdminUpdateSignatoriesAsync(AdminUpdateSignatoriesRequest request, int actorEid, int actorUserType);

    // Admin override of a single signatory row's sig_status by sig_id —
    // works on already-signed rows too (unlike AdminUpdateSignatoriesAsync).
    // Just flips the status value; does not touch document_signature_location
    // or re-run any of the notification/routing side effects a real sign does.
    Task<AdminUpdateSignatoryStatusResult> AdminUpdateSignatoryStatusAsync(AdminUpdateSignatoryStatusRequest request, int actorEid, int actorUserType);

    // Supporting documents are independent of the edit lock — always allowed.
    Task<AddSupportingFileResult> AddSupportingFileAsync(int docId, int eid, IFormFile file);

    Task<DeleteSupportingFileResult> DeleteSupportingFileAsync(int supId);

    // The signatory who can sign right away (lowest sig_order, ties broken by
    // lowest sig_id) — used to auto-mint a ready-to-sign public link right
    // after upload. Null if the document has no signatories.
    Task<(int Eid, int UserType)?> GetFirstSignatoryAsync(int docId);
}

public class DocumentViewResult
{
    public DocumentViewDto? Document { get; set; }
    public List<DocumentSignatoryViewDto> Signatories { get; set; } = new();
    public List<DocumentSignatoryViewGroupDto> Grouped { get; set; } = new();
}

public class SignatureImageResult
{
    public byte[]? Bytes { get; set; }
    public string? ContentType { get; set; }
    public bool NotFound { get; set; }
    public bool InvalidType { get; set; }
}

public class PdfDigitalOnlyResult
{
    public string? NotFoundMessage { get; set; }
    public string? BadRequestMessage { get; set; }
    public byte[]? PdfBytes { get; set; }
    // Sanitized "{description}.pdf" — caller (controller) builds the
    // Content-Disposition header from this plus its own isDownload param.
    public string? FileName { get; set; }
}

public class SaveSignatureResult
{
    public bool Success { get; set; }
    // True when the failure came from the outer catch-all (maps to 500);
    // false for the deliberate validation-style BadRequest returns (maps to 400).
    public bool IsServerError { get; set; }
    public string? Message { get; set; }
    // TEMP DEBUG: full exception text, Development-environment only — lets the
    // frontend console.log the real server-side error instead of just "Something
    // went wrong". Never populated (and never sent) outside Development.
    public string? DebugError { get; set; }

    // Populated only when Success is true.
    public string? DocId { get; set; }
    public string? DocCode { get; set; }
    public int NextSignatoryEid { get; set; }
    public int NextSignatoryUserType { get; set; }
    public string? SignerEid { get; set; }
    public string? SignerUserType { get; set; }
    public string? LegacyResult { get; set; }
}
