# Admin document-search dialog: full signatory manipulation + audit trail

Date: 2026-09-23
Status: approved for planning

## Context

`src/routes/_authenticated/admin/document-search.tsx` lets an admin search
any document and override it. Today the "Signatories" section of the edit
dialog is split in two:

- **Signed rows** (`sig_status != 0`): read-only name/order, status
  changeable via `DGSign/admin_update_signatory_status`. Nothing else about
  a signed row can be touched — `AdminUpdateSignatoriesAsync`
  (`SigningService.cs:2663`) only ever deletes and reinserts
  `sig_status = 0` rows, by explicit design (comment at
  `DGSignController.cs:720`): "already-signed rows are always preserved
  untouched."
- **Pending rows** (`sig_status == 0`): fully editable — order, count,
  delete, add (via `DynamicMultiSelect`) — already works today.

This spec extends full manipulation (status, order, delete, add) to signed
rows too, and adds an audit trail so every admin-initiated change to a
signatory — especially a destructive one on an already-signed row — is
recoverable: who did it, when, and what the row (and its signature-location
stamp, if any) looked like beforehand.

Also bundles three small, unrelated dialog fixes requested alongside this:
Doc Code becomes read-only, and the Document Status / Document Type
dropdowns show description-only (this second part was already shipped in
commit `9b4528a` — listed here for completeness, no further work needed).

**Scope boundary** (confirmed): audit logging covers only admin overrides
made from this dialog. The normal user-facing signing/returning flow
(`SigningService` outside the `Admin*` methods) is not touched and does not
write audit rows.

## Data model

New table, applied by hand per this repo's existing convention (see
`DigitalSignature/Sql/certificate_request_history.sql`) — a new file
`DigitalSignature/Sql/admin_signatory_audit.sql`:

```sql
CREATE TABLE [bacpdfsign].[dbo].[admin_signatory_audit] (
    id                    INT IDENTITY(1,1) PRIMARY KEY,
    doc_id                INT           NOT NULL,
    sig_id                INT           NULL,   -- NULL on 'add': row didn't exist before this action
    action                NVARCHAR(20)  NOT NULL, -- 'status_change' | 'order_change' | 'delete' | 'add'
    changed_by_eid        NVARCHAR(20)  NOT NULL,
    changed_by_user_type  NVARCHAR(10)  NOT NULL,
    changed_by_name       NVARCHAR(200) NULL,    -- denormalized snapshot of the admin's fname at time of change
    signatory_snapshot    NVARCHAR(MAX) NULL,    -- document_signatories row as JSON, taken BEFORE the change
    location_snapshot     NVARCHAR(MAX) NULL,    -- document_signature_location row(s) for sig_id as JSON,
                                                   -- only populated when the touched row was signed (sig_status = 1)
    changed_at            DATETIME      NOT NULL DEFAULT GETDATE()
);

CREATE INDEX IX_admin_signatory_audit_doc_id ON [bacpdfsign].[dbo].[admin_signatory_audit] (doc_id);
CREATE INDEX IX_admin_signatory_audit_sig_id ON [bacpdfsign].[dbo].[admin_signatory_audit] (sig_id);
```

One table with JSON snapshot columns (`NVARCHAR(MAX)`, parsed in C# —
no reliance on SQL Server JSON functions) rather than two tables mirroring
`document_signatories`/`document_signature_location` column-for-column.
Trade-off accepted: less ad-hoc-SQL-friendly than normalized columns, but
doesn't need a second migration every time either source table's schema
changes, and one row per action is enough for a simple "who changed what,
when" panel.

## Backend changes

### `DigitalSignature/Models/DGSignDtos.cs`

- `AdminSignatoryAuditDto`: `Id`, `DocId`, `SigId`, `Action`,
  `ChangedByEid`, `ChangedByName`, `SignatorySnapshot` (raw JSON string),
  `LocationSnapshot` (raw JSON string, nullable), `ChangedAt`. The frontend
  parses the two snapshot strings for display — no need to strongly type
  every historical field server-side.
- `AdminUpdateSignatoriesRequest` / `UploadSignatoryItemDto`: add
  `SigId` (nullable — present for an existing row being edited, null for a
  brand-new one) and `Status` (int, defaults preserved) so the endpoint can
  tell "update this row" apart from "insert new" without relying on the
  `new-` string-prefix convention currently used client-side only for React
  keys.

### `DigitalSignature/Services/ISigningService.cs` / `SigningService.cs`

Rewrite `AdminUpdateSignatoriesAsync`. Replace the current
delete-`sig_status=0`-then-reinsert-all strategy with a per-row diff,
inside one transaction:

1. Load current rows for `doc_id` (all statuses).
2. Rows present in current but missing from the submitted list → about to
   be **deleted**. Before deleting: snapshot the row and (if
   `sig_status == 1`) its `document_signature_location` rows into
   `admin_signatory_audit` (`action = 'delete'`), then
   `DELETE FROM document_signatories WHERE sig_id = @SigId`.
   `document_signature_location` rows are **never** deleted — only
   referenced by the audit snapshot — so a later `reconstruct_pdf` or
   report that joins on old `sig_id` values doesn't silently lose data
   (it just won't find the parent row anymore, same as any FK-by-
   convention orphan already tolerated elsewhere in this schema).
3. Rows present in both, with `Status`, `Order`, or `NumSignatures`
   changed → snapshot the row (audit `action` = `'status_change'` if
   status changed, else `'order_change'`; if multiple fields changed in
   one save, write one audit row per changed field so the log stays
   legible), then `UPDATE` in place.
4. Rows with no `SigId` (new) → `INSERT`, then one audit row
   (`action = 'add'`, `signatory_snapshot` = the row as inserted,
   `location_snapshot` = null).
5. Order validation (`request.Signatories.Any(s => s.Order > 0 && s.Order
   <= maxSignedOrder)`) is dropped — that check existed specifically to
   protect signed rows, which are no longer protected.

Add a small private helper `WriteSignatoryAuditAsync(tx, docId, sigId,
action, adminEid, adminUserType, adminName, signatoryRow, includeLocation)`
used by all four call sites above and by the existing
`AdminUpdateSignatoryStatusAsync` (also gets an audit write — it currently
does a bare `UPDATE ... SET sig_status`).

New method `GetAdminSignatoryAuditAsync(int docId)` →
`SELECT * FROM admin_signatory_audit WHERE doc_id = @DocId ORDER BY
changed_at DESC`.

### `DigitalSignature/Controllers/DGSignController.cs`

- `AdminUpdateSignatories` and `AdminUpdateSignatoryStatus`: pass the
  caller's `eid`/`UserType` claims (already read by
  `IsCurrentUserAdminAsync`'s sibling calls elsewhere in this file) down
  into the service call, plus the admin's `fname` (one extra
  `signatory_names` lookup, same pattern as other admin endpoints in this
  file).
- New `[HttpGet("admin_get_signatory_audit")]` → admin-gated, calls
  `GetAdminSignatoryAuditAsync`, returns the list.

## Frontend changes (`document-search.tsx`)

- **Doc Code**: `<Input value={docCode} ...>` → add `disabled`, drop the
  `onChange` (or keep state read-only). Field stays visible for reference.
- **Status/Type dropdowns**: already description-only as of `9b4528a` — no
  further change.
- **Signatories**: replace the two-block layout (locked `signedRows` list +
  editable `pendingRows` table) with one table driven by a single merged
  row list. Each row: Name (read-only), Status `Select` (all
  `sigStatusOptions`, not just the binary), Order `Input`, Count `Input`,
  Delete `Button`. The existing `DynamicMultiSelect` "Add pending
  signatories…" control stays, adds new rows to the same merged list.
  `PendingSignatoryRow`/`SignedSignatoryRow` types collapse into one
  `SignatoryRow` type carrying `sigId: number | string` (string `new-*`
  prefix retained client-side only for React keys, not sent as such to the
  backend — the request now sends `sigId: null` for those).
- **History panel**: new `Collapsible` (same pattern as the outer Filter
  card) below the signatories table, fetches
  `admin_get_signatory_audit?docId=` when the dialog opens, renders one row
  per audit entry: timestamp, admin name, action, and a one-line
  before-summary parsed from `signatory_snapshot`/`location_snapshot`.
  Read-only, no editing here.

## Error handling

- Save stays atomic: one transaction for the whole signatory diff (delete
  + update + insert + all their audit writes). Any failure rolls back
  everything, same as today's pattern in `AdminUpdateSignatoriesAsync`.
- Audit writes are not best-effort/fire-and-forget — they happen inside the
  same transaction as the mutation they document, so a mutation can never
  succeed without a matching audit row (or vice versa).
- If `admin_get_signatory_audit` fails to load, the History panel shows an
  inline retry (same `pdfError`/`Retry` pattern already used for the PDF
  preview pane), not a toast — it's supplementary, shouldn't block editing.

## Testing

- Manual, since this repo has no test runner wired up for either project
  (confirmed by existing workflow — changes are verified by the user's own
  publish-to-IIS step, not built/run locally in this session).
- Verification checklist for the implementer: edit a signed row's status,
  order, and delete it; add a brand-new signatory; confirm each produces
  exactly one `admin_signatory_audit` row with the expected `action` and a
  non-null `location_snapshot` only for the signed-row cases; confirm the
  History panel renders them; confirm Doc Code is not editable; confirm a
  failed save (e.g. simulate a DB error) leaves both `document_signatories`
  and `admin_signatory_audit` unchanged (transaction rollback).

## Files touched

- `DigitalSignature/Sql/admin_signatory_audit.sql` (new)
- `DigitalSignature/Models/DGSignDtos.cs`
- `DigitalSignature/Services/ISigningService.cs`
- `DigitalSignature/Services/SigningService.cs`
- `DigitalSignature/Controllers/DGSignController.cs`
- `src/routes/_authenticated/admin/document-search.tsx`
