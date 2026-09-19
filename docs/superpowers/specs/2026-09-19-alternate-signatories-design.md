# Alternate Signatories — Design

## Purpose

Let an employee designate another employee as their alternate signatory, so the
alternate can sign specific document types on the principal's behalf when the
principal is unavailable. A person can be the alternate for multiple
principals at once, and a principal can only have their own alternates manage
their own queue — this is purely self-service, not admin-managed.

## Data Model

`bacpdfsign.dbo.alternate_signatories` already exists:

| column | meaning |
|---|---|
| `id` | PK |
| `user_eid` / `user_type` | the principal (owner of the document queue) |
| `user_alternate_eid` / `user_alternate_type` | the alternate |
| `isactive` | bit |
| `is_permanent` | bit — if 1, ignore date range |
| `dateFrom` / `dateTo` | validity window when not permanent |

`bacpdfsign.dbo.alternate_signatories_documents` **also already exists**,
with old/existing data we can build on:

| column | meaning |
|---|---|
| `id` | PK |
| `alter_id` | FK → `alternate_signatories.id` |
| `doc_type_id` | FK → `document_types.id` |
| `datentime` | when the doc type was assigned |
| `is_alt_des_id` | existing column, purpose to be confirmed against real rows during implementation — treated as an internal/backend-set field, not surfaced in the UI |

Both tables predate this feature and already hold data — no schema changes
needed. Since no FK constraint is assumed to exist between the two tables,
"deleting an alternate cascades its document-type rows" (per the
add/edit/delete requirements) is enforced in application code: the
`delete_alternate` endpoint deletes matching `alternate_signatories_documents`
rows for that `alter_id` before deleting the `alternate_signatories` row
itself, inside one transaction.

## Backend

New `AlternateSignatoriesController` (`api/AlternateSignatories`), following
the existing pattern in this codebase: Dapper raw SQL via `IDatabaseService`,
identity resolved from JWT claims (`ClaimTypes.NameIdentifier`, `"UserType"`)
rather than trusting client-supplied principal identity — mirrors
`DGSignController`/`AuthController`.

- `GET my_alternates` — alternates *I* (caller) have set, joined to
  `signatory_names` for display name/office.
- `POST add_alternate` — body: alternate eid/user_type, isPermanent,
  dateFrom/dateTo. Rejects self-selection and exact duplicates.
- `PUT update_alternate/{id}` — isPermanent / dateFrom / dateTo only (not the
  person — changing who requires delete + re-add). Scoped to rows owned by
  the caller.
- `DELETE delete_alternate/{id}` — hard delete, scoped to caller-owned rows,
  cascades document-type rows via FK.
- `GET alternate_document_types/{alterId}` — doc types assigned to one
  alternate row (scoped to caller-owned `alterId`).
- `POST add_document_type` / `DELETE delete_document_type/{id}` — manage
  those assignments (scoped to caller-owned parent row).
- `GET my_principals` — people who have set *me* as their **active**
  alternate right now: `isactive = 1 AND (is_permanent = 1 OR
  GETDATE() BETWEEN dateFrom AND dateTo)`. Backs both the sidebar visibility
  check and the dropdown on the alternate queue page.
- `GET pending_documents_as_alternate?principalEid=&principalUserType=` —
  re-validates the caller is currently an active alternate for that
  principal (same predicate as `my_principals`), collects the doc type IDs
  assigned to that alternate row, then calls the existing
  `SigningService.GetPendingDocumentsAsync(year, principalEid,
  principalUserType, docTypeId: null)` and filters results to only those
  doc type IDs.

Employee picker for "add alternate" reuses the existing
`references/get_listofSignatories` endpoint (already returns name, office,
eid, user_type — the same source `uploadform.tsx` uses today for signatory
selection). Document-type picker reuses the existing
`references/get_document_types` endpoint.

## Frontend — Manage Alternates (on "My Signature")

Adds a new "Alternate Signatories" card/section to
`src/routes/_authenticated/signature/MySignature.tsx`, alongside its existing
specimen/certificate cards.

- **Grid** (`Table`): Name, Office, Status (Active/Inactive badge derived
  from `isactive` + date window), Validity ("Permanent" or a date range),
  and two right-aligned actions: **Document Types** and **Delete**.
- **Add**: dialog with an employee picker (single pick, built on the same
  fetch/search/virtualized-list UI as `DynamicMultiSelect`, pointed at
  `get_listofSignatories`, but constrained to one selection), a **Permanent**
  toggle, and date-range fields (shown only when not permanent, using the
  existing `Calendar`/date UI components). Blocks selecting yourself or a
  person already added.
- **Edit**: same dialog, pre-filled, editing only permanent/date-range.
- **Delete**: hard delete behind a confirm step.
- **Document Types dialog** (per row): lists currently-assigned doc types
  (each with a delete icon) plus a `DynamicMultiSelect` against
  `references/get_document_types` to add more. Full add/view/delete in one
  dialog.

## Frontend — "For Signature (Alternate)"

**Sidebar** (`app-sidebar.tsx`): the current flat "For Signature" / "My
Signature" items become a collapsible "Signature" group (same shape as the
existing "Admin" group in `nav-main.tsx`). A third item, **"For Signature
(Alternate)"**, is included in that group only when `GET my_principals`
returns at least one row — fetched once on sidebar load, following the same
"fetch a gate flag, conditionally render" convention as `check_admin` /
`check_vip` elsewhere in the app.

**New page**: `ForSignatureAlternateQueueView`, a copy of the existing
`ForSignatureQueueView` (`src/View/dgsign/ForSignatureQueueView.tsx`), with
one addition at the very top: a dropdown populated from `my_principals`,
defaulting to the first entry. Changing the dropdown reloads the queue via
`pending_documents_as_alternate` for the newly selected principal. This is
how, e.g., employee 3 — set as alternate by both employee 1 and employee 2 —
switches between their two queues from the same page.

**Signing wiring**: `ForSignatureQueueView` currently hardcodes
`isAlternate: 0` when submitting a signature
(`src/View/dgsign/ForSignatureQueueView.tsx` ~line 1035). The alternate copy
instead sends `isAlternate: 1`, `vwEids` / `vwUserType` = the selected
principal's eid/user_type, while `eid` / `userType` remain the logged-in
alternate's own identity (their own certificate/PIN/specimen are used to
sign). This exercises an already-built but previously unused branch in
`SigningService.cs` (`GetSigIdAlternate`, `GetAlternateSignatureImage`,
`sign_is_alternate_signature`) — no changes needed to that signing logic,
only the new list/validation endpoint above feeding it the right
document set.

## Scope for this iteration

- Display/sign only for the alternate flow (no delegate-flow changes).
- No admin-facing oversight view of all alternates system-wide — this is
  purely self-service per employee, consistent with `alternate_signatories`
  having no admin CRUD today.
- No email/notification when someone adds you as their alternate.
