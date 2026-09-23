using Microsoft.AspNetCore.Http;

namespace DigitalSignature.Models
{
    // ==================== Request / Response DTOs ====================
    // Moved out of DGSignController.cs so both DGSignController and
    // PublicSignController (and ISigningService/SigningService) can share
    // the same types. Pure relocation — no field/behavior changes.

    // Multipart counterpart of GenerateCertificateRequest — bound via
    // [FromForm] on submit_certificate_request, since it (unlike
    // generate_certificate) also carries the signature/valid-ID files.
    public class SubmitCertificateRequestForm
    {
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Organisation { get; set; } = "";
        public string Locality { get; set; } = "";
        public string Province { get; set; } = "";
        public string TwoLetter { get; set; } = "";
        public string Passwords { get; set; } = "";
        public string CpNumbers { get; set; } = "";
        // Legacy OTP field — no OTP step exists in this flow, so the
        // frontend always sends it empty. Nullable so ASP.NET Core's
        // implicit-required-for-non-nullable-strings validation (Nullable
        // enable + [ApiController]) doesn't reject the empty string.
        public string? DOtp { get; set; } = "";
        public string PinCode { get; set; } = "";
        public string Eid { get; set; } = "";
        public string UserType { get; set; } = "";
        public IFormFile SignatureImg { get; set; } = default!;
        public IFormFile? InitialSignatureImg { get; set; }
        public List<IFormFile> ValidIds { get; set; } = new();
        // Only used by resubmit_certificate_request (0 = new request via
        // submit_certificate_request, which ignores this field).
        public int RegId { get; set; }
    }

    public class CertificateRequestActionRequest
    {
        public int RegId { get; set; }
        // Admin remarks — used by reject (optional) and return-for-revision
        // (the reason the user needs to fix something), stored in the
        // previously-unused certificate_registration.user_reason column.
        public string? Remarks { get; set; }
    }

    // Multipart form for upload_certificate — registers an already-issued
    // .p12 the user already has (as opposed to submit_certificate_request,
    // which has the server generate a brand-new self-signed one). No OTP
    // field by design — unlike the legacy request flow, this one isn't
    // gated behind an SMS/email OTP confirmation step.
    public class UploadCertificateForm
    {
        public string Eid { get; set; } = "";
        public string UserType { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Organisation { get; set; } = "";
        public string Locality { get; set; } = "";
        public string Province { get; set; } = "";
        public string TwoLetter { get; set; } = "";
        public string CpNumbers { get; set; } = "";
        public string Password { get; set; } = ""; // .p12 password
        public string PinCode { get; set; } = "";
        public IFormFile P12File { get; set; } = default!;
        public IFormFile SignatureImg { get; set; } = default!;
        public IFormFile? InitialSignatureImg { get; set; }
        // Only used by resubmit_uploaded_certificate (0 = new upload via
        // upload_certificate, which ignores this field).
        public int RegId { get; set; }
    }

    // Multipart form for update_signature_specimen — replaces the signature/
    // initial image on an existing active certificate without regenerating
    // the certificate itself.
    public class UpdateSignatureSpecimenForm
    {
        public string Eid { get; set; } = "";
        public string UserType { get; set; } = "";
        public string Type { get; set; } = ""; // "signature" or "initial"
        public IFormFile Image { get; set; } = default!;
    }

    public class PendingCertificateRequestDto
    {
        public int RegId { get; set; }
        public string Eid { get; set; } = "";
        public string UserType { get; set; } = "";
        public string FullName { get; set; } = "";
        // From signatory_names (eid/user_type match) — the authoritative
        // name/position on record, as opposed to FullName which is just
        // whatever the applicant typed into the request form.
        public string? SignatoryName { get; set; }
        public string? Position { get; set; }
        public string? OfficeName { get; set; }
        public string Email { get; set; } = "";
        public string Organisation { get; set; } = "";
        public string Locality { get; set; } = "";
        public string Province { get; set; } = "";
        public string CpNumbers { get; set; } = "";
        public DateTime RequestDate { get; set; }
        public DateTime? DecidedDate { get; set; }
        public int Status { get; set; }
        // 0 = Requested (server-generated), 1 = Uploaded (already had a cert)
        public int Source { get; set; }
        // Admin remarks — set when Status is Rejected or Returned.
        public string? Remarks { get; set; }
        public bool HasSignature { get; set; }
        public int ValidIdCount { get; set; }
    }

    // One row of the admin "Active Certificates" list — pfx_attachments
    // rows directly, independent of the certificate_registration
    // request/approval workflow (a row can exist here with RegId = 0,
    // meaning it was never provisioned through this app's own request/
    // upload flow at all).
    public class ActivePfxCertificateDto
    {
        public int Id { get; set; }
        public string Eid { get; set; } = "";
        public string UserType { get; set; } = "";
        public string? SignatoryName { get; set; }
        public string? Position { get; set; }
        public string? OfficeName { get; set; }
        public string Code { get; set; } = "";
        public bool Active { get; set; }
        public int RegId { get; set; }
        // "DICT" when RegId = 0 (no linked certificate_registration row —
        // provisioned outside this app's own request/approval flow),
        // "PGAS" otherwise. Computed in SQL, not here.
        public string Source { get; set; } = "";
        public bool HasPassword { get; set; }
    }

    public class SetPfxActiveRequest
    {
        public int Id { get; set; }
        public bool Active { get; set; }
    }

    public class UpdatePfxPinCodeRequest
    {
        public int Id { get; set; }
        public string NewPinCode { get; set; } = "";
    }

    public class RemovePfxPasswordRequest
    {
        public int Id { get; set; }
    }

    // One row of a person's certificate_registration history — same shape as
    // PendingCertificateRequestDto minus the fields only the pending-review
    // list needs (name/contact details, signature/valid-ID counts).
    public class CertificateHistoryEntryDto
    {
        public int RegId { get; set; }
        public DateTime RequestDate { get; set; }
        public DateTime? DecidedDate { get; set; }
        public int Status { get; set; }
        public int Source { get; set; }
        public string? Remarks { get; set; }
    }

    // One row from signature_specimen_history — a Change Specimen event.
    public class SpecimenHistoryEntryDto
    {
        public string SpecimenType { get; set; } = "";
        public DateTime ChangedAt { get; set; }
    }

    // One row of a valid-ID attachment for a certificate request.
    public class CertificateValidIdDto
    {
        public int Id { get; set; }
        public string FileName { get; set; } = "";
    }

    public class GenerateCertificateRequest
    {
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Organisation { get; set; } = "";
        public string Locality { get; set; } = "";
        public string Province { get; set; } = "";
        public string TwoLetter { get; set; } = "";
        public string Passwords { get; set; } = "";
        public string CpNumbers { get; set; } = "";
        public string DOtp { get; set; } = "";
        public string PinCode { get; set; } = "";
        public string Eid { get; set; } = "";
        public string UserType { get; set; } = "";
        public string? SignatureBase64 { get; set; } // PNG as base64 string (optional)
    }

    public class DocumentSignatoryDto
    {
        public long SigId { get; set; }
        public int DocId { get; set; }
        public string? SigCode { get; set; }
        public int SigEid { get; set; }
        public int SigStatus { get; set; }
        public int SigOrder { get; set; }
        public string? SigRemarks { get; set; }
        public int SigUserType { get; set; }
        public int SigLevel { get; set; }
        public int SigSignCount { get; set; }
        public string? SigRemarksDatenTime { get; set; }
        public string? DateTimeInserted { get; set; }

        // From req_sign_status
        public int SignStatusId { get; set; }
        public string? SignStatusDescription { get; set; }

        // From signatory_names
        public string? Fname { get; set; }
        public string? Position { get; set; }
        public string? OfficeName { get; set; }
    }

    public class DocumentSignatoryGroupDto
    {
        public int SigOrder { get; set; }
        public int SignatoryCount { get; set; }
        public List<DocumentSignatoryDto> Signatories { get; set; } = new();
    }

    // ==================== Document View DTOs (get_document_view) ====================

    public class DocumentViewDto
    {
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public string? DocCode { get; set; }
        public string? DocDescription { get; set; }
        public int DocStatusId { get; set; }
        public string? DocStatusName { get; set; }
        public long DocTypeId { get; set; }
        public string? DocTypeName { get; set; }
        public string? DocTypeAbbr { get; set; }
        public int IsBulk { get; set; }
        public int IsShow { get; set; }
        public string? DocDatetime { get; set; }

        /// <summary>Parsed upload timestamp — used server-side to seed the first sig_order group's "received" date. Not for display; use <see cref="DocDatetime"/> for that.</summary>
        public DateTime? DocDatetimeParsed { get; set; }
        public int? DocPages { get; set; }
        public int DocEid { get; set; }
        public int DocEidUserType { get; set; }
        public string? UploadedByName { get; set; }
        public string? UploadedByPosition { get; set; }
        public string? UploadedByOffice { get; set; }
    }

    public class DocumentSignatoryViewDto
    {
        public long SigId { get; set; }
        public int DocId { get; set; }
        public string? SigCode { get; set; }
        public int SigEid { get; set; }
        public int SigStatus { get; set; }
        public int SigOrder { get; set; }
        public string? SigRemarks { get; set; }
        public int SigUserType { get; set; }
        public int SigLevel { get; set; }
        public int SigSignCount { get; set; }
        public string? SigRemarksDatenTime { get; set; }
        public string? DateTimeInserted { get; set; }

        // From req_sign_status
        public int SignStatusId { get; set; }
        public string? SignStatusDescription { get; set; }

        // From signatory_names
        public string? Fname { get; set; }
        public string? Position { get; set; }
        public string? OfficeName { get; set; }

        /// <summary>Latest document_signature_location.sign_datetime for this sig_id, if any.</summary>
        public DateTime? SignDatetime { get; set; }

        /// <summary>When this sig_order group received the document: doc_datetime for the first group, else the previous group's last signature.</summary>
        public DateTime? ReceivedDatetime { get; set; }

        /// <summary>Calendar days from ReceivedDatetime to SignDatetime once signed (turnaround time); otherwise to now (still pending).</summary>
        public int? DaysPending { get; set; }

        /// <summary>Set only when sig_status = 1 with no signature stamp of their own, and exactly one sibling in the same sig_order group actually signed for the group.</summary>
        public string? CounterSignedByName { get; set; }
        public string? CounterSignedByPosition { get; set; }
    }

    public class DocumentSupportViewDto
    {
        public int SupId { get; set; }
        public int DocId { get; set; }
        public string? SupDocName { get; set; }
        public string? SupType { get; set; }
        public string? SupDatetme { get; set; }
        public string? SupLocation { get; set; }
    }

    public class DocumentEditViewResult
    {
        public DocumentViewDto? Document { get; set; }
        public List<DocumentSignatoryViewDto> Signatories { get; set; } = new();
        public List<DocumentSupportViewDto> SupportingDocuments { get; set; } = new();
        // True only when every document_signatories.sig_status for this doc is 0
        // (nobody has signed yet) — gates whether UpdateDocumentAsync will accept changes.
        public bool CanEditMain { get; set; }
    }

    public class UpdateDocumentResult
    {
        public bool Success { get; set; }
        public bool IsServerError { get; set; }
        // True when rejected specifically because a signatory has already signed —
        // maps to 409 Conflict, distinct from a plain validation 400.
        public bool IsLocked { get; set; }
        public string? Message { get; set; }
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public string? DocDirectory { get; set; }
        public int DocPages { get; set; }
        public int SignatoryCount { get; set; }
    }

    // One row of the admin document-search results — admin_search_documents.
    public class AdminDocumentSearchResultDto
    {
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public string? DocDescription { get; set; }
        public string? DocCode { get; set; }
        public int DocStatusId { get; set; }
        public int? DocTypeId { get; set; }
        public int DocEid { get; set; }
        public int DocEidUserType { get; set; }
        public string? DocDatetime { get; set; }
        public string? DocDatetimeUpdate { get; set; }
        public string? DocumentTypeName { get; set; }
        public string? StatusType { get; set; }
        public string? OwnerName { get; set; }
        public string? OwnerOffice { get; set; }
    }

    // admin_update_document — an admin-only override edit of document_attach
    // fields UpdateDocumentAsync deliberately never touches (doc_code,
    // doc_status_id), with no signed-signatory lock. Leaves
    // document_signatories untouched entirely (see AdminUpdateSignatoriesRequest).
    public class AdminUpdateDocumentRequest
    {
        public int DocId { get; set; }
        public string? Description { get; set; }
        public string? DocCode { get; set; }
        public int? DocStatusId { get; set; }
        public int? DocTypeId { get; set; }
    }

    public class AdminUpdateDocumentResult
    {
        public bool Success { get; set; }
        public bool IsServerError { get; set; }
        public string? Message { get; set; }
        public int DocId { get; set; }
        public int DocPages { get; set; }
    }

    // admin_update_signatories — full per-row diff against the submitted
    // list: rows missing from the list are deleted, rows present with
    // changed Status/Order/NumSignatures are updated, rows with SigId ==
    // null are inserted. Works on already-signed rows too (unlike the
    // previous pending-only behavior) — every touched row that was signed
    // gets an admin_signatory_audit snapshot first. See
    // docs/superpowers/specs/2026-09-23-admin-signatory-audit-design.md.
    public class AdminSignatoryItemDto
    {
        public int? SigId { get; set; }
        public int Eid { get; set; }
        public int UserType { get; set; }
        public int Order { get; set; } = 1;
        public int NumSignatures { get; set; } = 1;
        public int? Level { get; set; }
        public int Status { get; set; }
        public string? QuerySigned { get; set; }
        public string? QueryReturn { get; set; }
    }

    public class AdminUpdateSignatoriesRequest
    {
        public int DocId { get; set; }
        public List<AdminSignatoryItemDto> Signatories { get; set; } = new();
    }

    public class AdminUpdateSignatoriesResult
    {
        public bool Success { get; set; }
        public bool IsServerError { get; set; }
        public string? Message { get; set; }
        public int SignatoryCount { get; set; }
    }

    // Admin override of one signatory row's sig_status by sig_id — a
    // direct write, used by the dialog's per-row Select for immediate
    // apply (order/delete/add go through AdminUpdateSignatoriesAsync's
    // batch Save instead). Writes one admin_signatory_audit row.
    public class AdminUpdateSignatoryStatusRequest
    {
        public int SigId { get; set; }
        public int Status { get; set; }
    }

    public class AdminUpdateSignatoryStatusResult
    {
        public bool Success { get; set; }
        public bool IsServerError { get; set; }
        public string? Message { get; set; }
    }

    // Full current document_signatories row — used by
    // AdminUpdateSignatoriesAsync to diff the submitted list against what's
    // actually in the database, and to build a signatory_snapshot JSON blob.
    public class DocumentSignatoryFullRowDto
    {
        public int SigId { get; set; }
        public int DocId { get; set; }
        public string? SigCode { get; set; }
        public int SigEid { get; set; }
        public int SigUserType { get; set; }
        public int SigStatus { get; set; }
        public int SigOrder { get; set; }
        public string? SigRemarks { get; set; }
        public string? SigQuerySigned { get; set; }
        public string? SigQueryReturn { get; set; }
        public int? SigLevel { get; set; }
        public int SigSignCount { get; set; }
        public string? SigRemarksDatenTime { get; set; }
        public string? DateTimeInserted { get; set; }
    }

    // One row from admin_signatory_audit — a single admin-initiated change
    // to one signatory. SignatorySnapshot/LocationSnapshot are raw JSON
    // strings; the frontend parses them for display.
    public class AdminSignatoryAuditDto
    {
        public int Id { get; set; }
        public int DocId { get; set; }
        public int? SigId { get; set; }
        public string Action { get; set; } = "";
        public string ChangedByEid { get; set; } = "";
        public string ChangedByUserType { get; set; } = "";
        public string? ChangedByName { get; set; }
        public string? SignatorySnapshot { get; set; }
        public string? LocationSnapshot { get; set; }
        public DateTime ChangedAt { get; set; }
    }

    public class DeleteDocumentResult
    {
        public bool Success { get; set; }
        public bool IsServerError { get; set; }
        public string? Message { get; set; }
        // DB rows were removed successfully but the physical folder delete
        // failed (e.g. a locked file) — not a failure, just a cleanup note.
        public bool PhysicalCleanupWarning { get; set; }
    }

    public class AddSupportingFileResult
    {
        public bool Success { get; set; }
        public bool IsServerError { get; set; }
        public string? Message { get; set; }
        public int SupId { get; set; }
        public string? SupDocName { get; set; }
        public string? SupLocation { get; set; }
    }

    public class DeleteSupportingFileResult
    {
        public bool Success { get; set; }
        public bool IsServerError { get; set; }
        public string? Message { get; set; }
    }

    public class FirstSignatoryDto
    {
        public int Eid { get; set; }
        public int UserType { get; set; }
    }

    public class DocumentSignatoryViewGroupDto
    {
        public int SigOrder { get; set; }
        public int SignatoryCount { get; set; }
        public List<DocumentSignatoryViewDto> Signatories { get; set; } = new();
    }

    public class SigSignDateDto
    {
        public long SigId { get; set; }
        public DateTime? SignDatetime { get; set; }
    }

    // ==================== Get Documents For Signatory DTOs ====================

    public class SignatoryDocumentListItemDto
    {
        // Document details
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public string? DocCode { get; set; }
        public string? DocDescription { get; set; }
        public int DocStatusId { get; set; }
        public string? DocStatusName { get; set; }
        public long DocTypeId { get; set; }
        public string? DocTypeName { get; set; }
        public string? DocTypeAbbr { get; set; }
        public string? DocDatetime { get; set; }
        public int? DocPages { get; set; }

        // This signatory's own single data point — not the full signatory chain
        public long SigId { get; set; }
        public int SigOrder { get; set; }
        public int SigStatus { get; set; }
        public string? SigStatusName { get; set; }
        public DateTime? ReceivedDatetime { get; set; }
        public DateTime? SignDatetime { get; set; }

        /// <summary>Calendar days from ReceivedDatetime to SignDatetime once signed (turnaround time); otherwise to now (still pending).</summary>
        public int? DaysPending { get; set; }
    }

    public class AllSignatoryOrderDto
    {
        public int DocId { get; set; }
        public long SigId { get; set; }
        public int SigOrder { get; set; }
    }

    public class DashboardStatusBucketDto
    {
        public string? Label { get; set; }
        public int Count { get; set; }
    }

    public class DashboardPersonalSummaryDto
    {
        public int Signed { get; set; }
        public int Unsigned { get; set; }
        public int Returned { get; set; }
        public int Uploaded { get; set; }
    }


    public class PendingDocumentCoreDto
    {
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public string? DocCode { get; set; }
        public string? DocDescription { get; set; }
        public string? DocCreatedDatetime { get; set; }
        public int DocEid { get; set; }
        public int DocEidUserType { get; set; }
        public int DocTypeId { get; set; }
        public int DocStatusId { get; set; }
        public string? DocStatusName { get; set; }
        public string? DocTypeName { get; set; }
        public string? DocTypeAbbr { get; set; }
        public int SigId { get; set; }
        public int SigOrder { get; set; }
        public int SigEid { get; set; }
        public int SigUserType { get; set; }
        public int SigStatus { get; set; }
        public int SigLevel { get; set; }         // added
        public int SigSignCount { get; set; }     // added
        public string? SigStatusName { get; set; }
        public string? SignatoryAssignedDatetime { get; set; }
        public string? CurrentSignatureDatetime { get; set; }
    }

    public class SignatoryNameDto
    {
        public int Eid { get; set; }
        public int UserType { get; set; }
        public string? Fname { get; set; }
        public string? Position { get; set; }
        public string? OfficeName { get; set; }
    }

    public class SignatoryListDto
    {
        public int Eid { get; set; }
        public string? Fname { get; set; }
        public string? Position { get; set; }
        public string? Offices { get; set; }
        public string? OfficeName { get; set; }
        public string? Email { get; set; }
        public int UserType { get; set; }
        public string? Cp { get; set; }
        public int Active { get; set; }
        public string? MultiId { get; set; }
        public string? FEmailAddress { get; set; }
    }

    public class LastActionDto
    {
        public int DocId { get; set; }
        public string? LastActionDatetime { get; set; }
        public string? LastActionType { get; set; }
        public int ActorEid { get; set; }
        public int ActorUserType { get; set; }
    }

    public class SaveSignatureRequest
    {
        public string DocId { get; set; } = "";
        public string Eid { get; set; } = "";
        public string UserType { get; set; } = "";
        /// <summary>Structured signature locations (preferred). Each item carries page, position, specimen type, and authority level.</summary>
        public List<SignatureLocationItem>? Signatures { get; set; }
        /// <summary>Legacy comma/double-underscore delimited location string. Used only when Signatures is null/empty.</summary>
        public string? Location { get; set; }
        public string BulkPasswords { get; set; } = "";
        public string BulkDeviceType { get; set; } = "";
        public string ModsId { get; set; } = "";
        public int IsDisplayDate { get; set; } = 0;
        public int IsDelegate { get; set; } = 0;
        public int IsAlternate { get; set; } = 0;
        public string VwEids { get; set; } = "";
        public string VwUserType { get; set; } = "";
        public string DateNTimeClick { get; set; } = "";
        public string DgLatitude { get; set; } = "";
        public string DgLongitude { get; set; } = "";
        public string DgAccuracy { get; set; } = "";
        public string DgDeviceType { get; set; } = "";
        public string DgAddress { get; set; } = "";
        /// <summary>
        /// PIN code mode: if supplied, the PIN is verified against pfx_attachments.pin_code
        /// and the stored encrypted password is used. Takes priority over BulkPasswords.
        /// </summary>
        public string PinCode { get; set; } = "";
    }

    /// <summary>A single signature placement on a PDF page.</summary>
    public class SignatureLocationItem
    {
        /// <summary>1-based page number.</summary>
        public int Page { get; set; }
        /// <summary>Horizontal position as a 0-1 fraction of page width.</summary>
        public float XPct { get; set; }
        /// <summary>Vertical position as a 0-1 fraction of page height.</summary>
        public float YPct { get; set; }
        /// <summary>0 = main signature image, 1 = alternate/initial specimen.</summary>
        public int SpecimenType { get; set; }
        /// <summary>1 = default, 3 = By Authority of the Governor, 4 = For.</summary>
        public int AuthorityLevel { get; set; } = 1;
    }

    public class PfxDetails
    {
        public string PfxId { get; set; } = "";
        public byte[]? PfxAttachment { get; set; }
        public string? Password { get; set; }
        public string? Signature { get; set; }
        public string? Eid { get; set; }
        public string? Code { get; set; }
        public string? Fname { get; set; }
    }

    public class NextSignatoryDto
    {
        public int SigEid { get; set; }
        public int SigUserType { get; set; }
    }

    public class PendingDocumentDto
    {
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public string? DocCode { get; set; }
        public string? DocDescription { get; set; }
        public int DocTypeId { get; set; }
        public string? DocCreatedDatetime { get; set; }
        public string? DocCreatedByName { get; set; }
        public string? DocCreatedByPosition { get; set; }
        public string? DocCreatedByOffice { get; set; }
        public int DocStatusId { get; set; }
        public string? DocStatusName { get; set; }
        public string? DocTypeName { get; set; }
        public string? DocTypeAbbr { get; set; }
        public int SigId { get; set; }
        public int SigOrder { get; set; }
        public int SigEid { get; set; }
        public int SigUserType { get; set; }
        public string? SignatoryName { get; set; }
        public string? SignatoryPosition { get; set; }
        public string? SignatoryOffice { get; set; }
        public int SigStatus { get; set; }
        public string? SigStatusName { get; set; }
        public string? SignatoryAssignedDatetime { get; set; }
        public string? CurrentSignatureDatetime { get; set; }
        public string? LastActionDatetime { get; set; }
        public string? LastActionType { get; set; }
        public string? LastActionByName { get; set; }
        public string? LastActionByPosition { get; set; }
        public string? LastActionByOffice { get; set; }
        public int SigLevel { get; set; }         // added
        public int SigSignCount { get; set; }     // added
    }

    public class PendingDocumentTypeDto
    {
        public int Id { get; set; }
        public string? DocumentDescription { get; set; }
        public int PendingCount { get; set; }
    }

    public class DocListDto
    {
        public long DocId { get; set; }
        public string? DocName { get; set; }
        public int DocType { get; set; }
        public string? DocDescription { get; set; }
        public int DocStatusId { get; set; }
        public long DocEid { get; set; }          // doc_eid
        public string? DocCode { get; set; }
        public int DocSignatoryType { get; set; }
        public string? DocTypeId { get; set; }
        public int DocEidUserType { get; set; }   // doc_eid_user_type
        public int DocIs { get; set; }
        public string? DocTypeDesc { get; set; }  // document_description alias
        public string? StatusType { get; set; }   // status_type from req_status
        public string? Preparer { get; set; }
        public string? Office { get; set; }       // office_name alias "office"
        public DateTime? DbDatetime { get; set; } // db_datetime
    }

    public class SignedHistoryDto
    {
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public string? DocCode { get; set; }
        public string? DocDescription { get; set; }
        public string? DocCreatedDatetime { get; set; }
        public string? DocCreatedByName { get; set; }
        public string? DocCreatedByPosition { get; set; }
        public string? DocCreatedByOffice { get; set; }
        public int DocStatusId { get; set; }
        public string? DocStatusName { get; set; }
        public string? DocTypeName { get; set; }
        public string? DocTypeAbbr { get; set; }
        public int SigId { get; set; }
        public int SigOrder { get; set; }
        public int SigEid { get; set; }
        public int SigUserType { get; set; }
        public string? SignatoryName { get; set; }
        public string? SignatoryPosition { get; set; }
        public string? SignatoryOffice { get; set; }
        public int SigStatus { get; set; }
        public string? SigStatusName { get; set; }
        public string? SignatoryAssignedDatetime { get; set; }
        public string? UserActionDatetime { get; set; }
        public string? PreviousEventDatetime { get; set; }
        public string? TimeSincePrevious { get; set; }
    }

    public class ReturnedHistoryDto
    {
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public string? DocCode { get; set; }
        public string? DocDescription { get; set; }
        public string? DocCreatedDatetime { get; set; }
        public string? DocCreatedByName { get; set; }
        public string? DocCreatedByPosition { get; set; }
        public string? DocCreatedByOffice { get; set; }
        public int DocStatusId { get; set; }
        public string? DocStatusName { get; set; }
        public string? DocTypeName { get; set; }
        public string? DocTypeAbbr { get; set; }
        public int SigId { get; set; }
        public int SigOrder { get; set; }
        public int SigEid { get; set; }
        public int SigUserType { get; set; }
        public string? SignatoryName { get; set; }
        public string? SignatoryPosition { get; set; }
        public string? SignatoryOffice { get; set; }
        public int SigStatus { get; set; }
        public string? SigStatusName { get; set; }
        public string? SignatoryAssignedDatetime { get; set; }
        public string? UserActionDatetime { get; set; }
        public string? UserActionReason { get; set; }
        public int? ReturnType { get; set; }
        public string? PreviousEventDatetime { get; set; }
        public string? TimeSincePrevious { get; set; }
    }

    public class DocumentAttachDetails
    {
        public int DocId { get; set; }
        public int? DocIs { get; set; }
        public string? DocDescription { get; set; }
        public long DocTypeId { get; set; }
        public int DocType { get; set; }
        public string? DocDirectory { get; set; }
        public string? DocName { get; set; }
        public string? DocCode { get; set; }
        public int DocStatusId { get; set; }
        public byte[]? DocAttachment { get; set; }       // populated when column is varbinary
        public string? DocAttachmentText { get; set; }   // populated when column is varchar/base64
        public string? DocDatetime { get; set; }
        public int? DocPages { get; set; }               // only populated by callers that select it
    }

    // The principal's pending document_signatories row an alternate/delegate
    // signature is about to satisfy, fetched in full (not just its sig_id) so
    // SigningService.SaveSignatureImageAsync can clone it into a new row under
    // the alternate's own identity.
    public class PrincipalSignatoryRowDto
    {
        public int DocId { get; set; }
        public string? SigCode { get; set; }
        public int SigOrder { get; set; }
        public int SigLevel { get; set; }
        public int SigSignCount { get; set; }
        public string? SigQuerySigned { get; set; }
        public string? SigQueryReturn { get; set; }
    }

    // Lightweight projection used to lazily load just the blob columns of
    // document_attach, without pulling them into every DocumentAttachDetails query.
    public class DocumentAttachmentBlob
    {
        public byte[]? DocAttachment { get; set; }
        public string? DocAttachmentText { get; set; }
    }

    public class DocumentUploadedDto
    {
        public int DocId { get; set; }
        public string? DocName { get; set; }
        public int? DocType { get; set; }
        public string? DocDescription { get; set; }
        public int? DocStatusId { get; set; }
        public string? DocDesignated { get; set; }
        public string? DocDatetime { get; set; }
        public int DocEid { get; set; }
        public string? DocDatetimeUpdate { get; set; }
        public string? DocCode { get; set; }
        public string? DocSignatoryType { get; set; }
        public int? DocIs { get; set; }
        public int? DocTypeId { get; set; }
        public int? DocEidUserType { get; set; }
        public int? DocPages { get; set; }
        public string? DocTransaction { get; set; }
        public string? DocumentDescription { get; set; }
        public string? DocumentAbbr { get; set; }
        public string? StatusType { get; set; }
    }

    public class SignatoryStatusDto
    {
        public int SigOrder { get; set; }
        public int SigStatus { get; set; }
        public string? Fname { get; set; }
        public int SigEid { get; set; }
    }

    // ==================== PNPKI Expiration DTOs ====================

    public class PnpkiUserDto
    {
        public string PfxId { get; set; } = "";
        public string Eid { get; set; } = "";
        public int UserType { get; set; }
        public byte[]? PfxAttachment { get; set; }
        public string? Password { get; set; }
        public string? Code { get; set; }
        public string? Fname { get; set; }
        public string? Position { get; set; }
        public string? OfficeName { get; set; }
    }

    public class PnpkiExpirationDto
    {
        public string PfxId { get; set; } = "";
        public string Eid { get; set; } = "";
        public int UserType { get; set; }
        public string? Fname { get; set; }
        public string? Position { get; set; }
        public string? OfficeName { get; set; }
        public string? CertificateSubject { get; set; }
        public string? CertificateIssuer { get; set; }
        public DateTime? NotBefore { get; set; }
        public DateTime? NotAfter { get; set; }
        public string? SerialNumber { get; set; }
        public string? Thumbprint { get; set; }
        public int? DaysUntilExpiration { get; set; }
        public string Status { get; set; } = "";
        public string? ErrorMessage { get; set; }
    }
}
