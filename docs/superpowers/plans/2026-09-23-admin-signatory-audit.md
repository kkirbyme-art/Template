# Admin Signatory Full-CRUD + Audit Trail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an admin fully edit (status, order, delete, add) every signatory on a document from the Document Search dialog — including already-signed ones — while every such change is recorded in a new audit table with who/when/what-changed and a snapshot of the signing-location record for any signed row that's touched.

**Architecture:** `AdminUpdateSignatoriesAsync` moves from "delete+reinsert pending rows only" to a per-row diff against the submitted list (update/delete/insert as needed, any status), writing one `admin_signatory_audit` row per changed field inside the same transaction as the mutation. The dialog's frontend merges its previously-split "signed" (locked) and "pending" (editable) signatory lists into one editable table, adds a read-only History panel backed by a new `GET admin_get_signatory_audit` endpoint, and makes Doc Code read-only.

**Tech Stack:** ASP.NET Core (Dapper over `Microsoft.Data.SqlClient`), React + TanStack Router, shadcn/ui components. No automated test runner is wired up in this repo (confirmed in the spec) — every task's tests are manual verification steps (SSMS queries, curl/browser checks against the running app).

**Spec:** `docs/superpowers/specs/2026-09-23-admin-signatory-audit-design.md`

## Global Constraints

- Audit logging covers only admin-override actions from this dialog — the normal user signing/returning flow is never touched (spec "Scope boundary").
- `document_signature_location` rows are never deleted by this feature, even when their parent `document_signatories` row is deleted — only referenced by JSON snapshot (spec, backend changes §2).
- All signatory mutations for one Save (delete + update + insert) plus their audit writes happen in a single transaction — a mutation can never commit without its audit row, or vice versa (spec "Error handling").
- `sign_image` on `document_signature_location` is always stored as `''` (confirmed in `SigningService.cs:1466`) — safe to include verbatim in a location snapshot, no size concern.
- `sig_remarks_datenTime`, `date_time_inserted`, and `document_signature_location.sign_datetime` are all `NVARCHAR`, not native `DATETIME` columns — snapshot them as strings as-is, don't attempt to parse/convert.
- The `admin_signatory_audit` table already exists on the SQL Server instance (user confirmed) — Task 1 documents/commits the migration script for the record and verifies the live schema matches; it does not need to be run again.

## Review Focus

- **Deleting a row that was never signed but has an in-flight order dependency** — deleting a pending row with the lowest `sig_order` should not break the "next to sign" logic for the remaining rows; the diff must not renumber untouched rows.
- **Editing a brand-new (unsaved) row's status before the first Save** — a row with `sigId == null` has nothing to `admin_update_signatory_status`-PATCH yet; its status must flow through the batch `admin_update_signatories` insert instead, not silently drop.
- **Two admins (or one admin double-clicking) editing the same document concurrently** — not solved by this plan (no optimistic locking added), but the transaction boundary means a given Save is at least atomic with itself; call this out, don't silently pretend it's handled.
- **A signed row with zero `document_signature_location` rows** (edge case: status was flipped to 1 by a previous admin override without ever actually signing) — `location_snapshot` must come back `null`, not throw, when the location query finds nothing.
- **Re-saving with no actual changes** (admin opens dialog, changes nothing, clicks Save) — must not write spurious audit rows; the diff has to compare against current DB values, not just "was this row present in the request".

---

## File Structure

- `DigitalSignature/Sql/admin_signatory_audit.sql` (new) — table DDL, hand-applied migration record.
- `DigitalSignature/Models/DGSignDtos.cs` (modify) — new DTOs: `AdminSignatoryAuditDto`, `AdminSignatoryItemDto`, `DocumentSignatoryFullRowDto`; `AdminUpdateSignatoriesRequest.Signatories` retyped.
- `DigitalSignature/Services/ISigningService.cs` (modify) — signature changes for the two admin signatory methods, new `GetAdminSignatoryAuditAsync`.
- `DigitalSignature/Services/SigningService.cs` (modify) — `AdminUpdateSignatoriesAsync` rewrite, `AdminUpdateSignatoryStatusAsync` gets audit write, new `WriteSignatoryAuditAsync` helper, new `GetAdminSignatoryAuditAsync`.
- `DigitalSignature/Controllers/DGSignController.cs` (modify) — pass actor claims into the two existing calls, new `admin_get_signatory_audit` endpoint.
- `src/routes/_authenticated/admin/document-search.tsx` (modify) — Doc Code disabled, signatories UI merged into one table, new History panel.

---

## Task 1: SQL migration file + schema verification

**Files:**
- Create: `DigitalSignature/Sql/admin_signatory_audit.sql`

**Interfaces:**
- Produces: the `bacpdfsign.dbo.admin_signatory_audit` table, columns `id, doc_id, sig_id, action, changed_by_eid, changed_by_user_type, changed_by_name, signatory_snapshot, location_snapshot, changed_at`, used by every later task's SQL.

- [ ] **Step 1: Write the migration file**

```sql
-- Run this manually against the bacpdfsign database. Not applied by any
-- migration tooling — this repo manages schema for bacpdfsign by hand
-- (see certificate_request_history.sql for the same convention).
--
-- Backs the admin document-search dialog's full signatory CRUD: every
-- admin-initiated status/order/delete/add on a signatory writes one row
-- here, capturing who did it and a before-the-change snapshot of the
-- document_signatories row (and, if it was already signed, its
-- document_signature_location rows too).

CREATE TABLE [bacpdfsign].[dbo].[admin_signatory_audit] (
    id                    INT IDENTITY(1,1) PRIMARY KEY,
    doc_id                INT           NOT NULL,
    sig_id                INT           NULL,   -- NULL on 'add': row didn't exist before this action
    action                NVARCHAR(20)  NOT NULL, -- 'status_change' | 'order_change' | 'delete' | 'add'
    changed_by_eid        NVARCHAR(20)  NOT NULL,
    changed_by_user_type  NVARCHAR(10)  NOT NULL,
    changed_by_name       NVARCHAR(200) NULL,
    signatory_snapshot    NVARCHAR(MAX) NULL,
    location_snapshot     NVARCHAR(MAX) NULL,
    changed_at            DATETIME      NOT NULL DEFAULT GETDATE()
);

CREATE INDEX IX_admin_signatory_audit_doc_id ON [bacpdfsign].[dbo].[admin_signatory_audit] (doc_id);
CREATE INDEX IX_admin_signatory_audit_sig_id ON [bacpdfsign].[dbo].[admin_signatory_audit] (sig_id);
```

- [ ] **Step 2: Verify the live table matches**

Run against the SQL Server instance (SSMS or `sqlcmd`) — the table already
exists, this just confirms it matches the script above before any code
starts writing to it:

```sql
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'admin_signatory_audit'
ORDER BY ORDINAL_POSITION;
```

Expected: 10 rows — `id, doc_id, sig_id, action, changed_by_eid,
changed_by_user_type, changed_by_name, signatory_snapshot,
location_snapshot, changed_at` — with types matching the script. If
anything's off (missing column, wrong nullability), fix the live table to
match before continuing — every later task assumes this exact shape.

- [ ] **Step 3: Commit**

```bash
git add "DigitalSignature/Sql/admin_signatory_audit.sql"
git commit -m "Add admin_signatory_audit migration record"
```

---

## Task 2: DTOs

**Files:**
- Modify: `DigitalSignature/Models/DGSignDtos.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `AdminSignatoryAuditDto { Id, DocId, SigId, Action, ChangedByEid, ChangedByUserType, ChangedByName, SignatorySnapshot, LocationSnapshot, ChangedAt }` — used by Task 6 (read endpoint) and the frontend History panel (Task 10).
  - `AdminSignatoryItemDto { SigId (int?), Eid, UserType, Order, NumSignatures, Level, Status, QuerySigned, QueryReturn }` — replaces `UploadSignatoryItemDto` as the element type of `AdminUpdateSignatoriesRequest.Signatories`. Kept separate from `UploadSignatoryItemDto` (defined in `DocumentIntakeDtos.cs` and shared with the unrelated document-upload flow) so this admin-only change can't affect that flow.
  - `DocumentSignatoryFullRowDto { SigId, DocId, SigCode, SigEid, SigUserType, SigStatus, SigOrder, SigRemarks, SigQuerySigned, SigQueryReturn, SigLevel, SigSignCount, SigRemarksDatenTime, DateTimeInserted }` — used by Task 3 to read the full current row for diffing/snapshotting.

- [ ] **Step 1: Add the DTOs**

In `DigitalSignature/Models/DGSignDtos.cs`, replace the existing
`AdminUpdateSignatoriesRequest`/`AdminUpdateSignatoriesResult`/
`AdminUpdateSignatoryStatusRequest`/`AdminUpdateSignatoryStatusResult`
block (around line 370-403) with:

```csharp
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
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build DigitalSignature/DigitalSignature.csproj`
Expected: build succeeds (no other file references the old
`AdminUpdateSignatoriesRequest`/`Result` shapes yet other than
`ISigningService`/`SigningService`/`DGSignController`, which Tasks 3-4 fix
next — if this build fails here because of those files, that's expected
and resolved by the next tasks).

- [ ] **Step 3: Commit**

```bash
git add "DigitalSignature/Models/DGSignDtos.cs"
git commit -m "Add admin signatory audit + full-row DTOs"
```

---

## Task 3: `WriteSignatoryAuditAsync` helper + rewritten `AdminUpdateSignatoriesAsync`

**Files:**
- Modify: `DigitalSignature/Services/ISigningService.cs`
- Modify: `DigitalSignature/Services/SigningService.cs`

**Interfaces:**
- Consumes: `AdminSignatoryItemDto`, `DocumentSignatoryFullRowDto` (Task 2).
- Produces:
  - `Task<AdminUpdateSignatoriesResult> AdminUpdateSignatoriesAsync(AdminUpdateSignatoriesRequest request, int actorEid, int actorUserType)` — new signature, consumed by Task 5 (controller).
  - private `Task WriteSignatoryAuditAsync(IDbTransaction tx, int docId, int? sigId, string action, int actorEid, int actorUserType, string? actorName, DocumentSignatoryFullRowDto? signatoryRow, bool includeLocation)` — also consumed by Task 4.

- [ ] **Step 1: Update the interface signature**

In `DigitalSignature/Services/ISigningService.cs`, replace:

```csharp
    Task<AdminUpdateSignatoriesResult> AdminUpdateSignatoriesAsync(AdminUpdateSignatoriesRequest request);
```

with:

```csharp
    // Per-row diff against the submitted list — updates/deletes/inserts as
    // needed, any status (signed rows are no longer protected). Every
    // touched row that was already signed gets an admin_signatory_audit
    // snapshot (including its document_signature_location rows) before
    // being changed.
    Task<AdminUpdateSignatoriesResult> AdminUpdateSignatoriesAsync(AdminUpdateSignatoriesRequest request, int actorEid, int actorUserType);
```

Also add, near it:

```csharp
    Task<List<AdminSignatoryAuditDto>> GetAdminSignatoryAuditAsync(int docId);
```

(This second method is implemented in Task 6, declared here now so Task 4
and Task 5 both see the full interface surface.)

- [ ] **Step 2: Write `WriteSignatoryAuditAsync`**

In `DigitalSignature/Services/SigningService.cs`, add this private helper
near `AdminUpdateSignatoriesAsync` (replace the whole existing method in
the next step, but add this helper first):

```csharp
    // Snapshots one document_signatories row (and, if it's signed, its
    // document_signature_location rows) into admin_signatory_audit, before
    // the caller applies whatever change prompted the write. Must run
    // inside the same transaction as that change.
    private async Task WriteSignatoryAuditAsync(
        IDbTransaction tx,
        int docId,
        int? sigId,
        string action,
        int actorEid,
        int actorUserType,
        string? actorName,
        DocumentSignatoryFullRowDto? signatoryRow,
        bool includeLocation)
    {
        string? locationSnapshot = null;
        if (includeLocation && sigId.HasValue)
        {
            var locationRows = (await _dbService.QueryAsync<dynamic, dynamic>(
                "SELECT * FROM bacpdfsign.dbo.document_signature_location WHERE sig_id = @SigId",
                new { SigId = sigId.Value },
                CommandType.Text)).ToList();
            if (locationRows.Count > 0)
                locationSnapshot = System.Text.Json.JsonSerializer.Serialize(locationRows);
        }

        await _dbService.ExecuteAsync<dynamic>(
            @"INSERT INTO bacpdfsign.dbo.admin_signatory_audit
                (doc_id, sig_id, action, changed_by_eid, changed_by_user_type, changed_by_name,
                 signatory_snapshot, location_snapshot, changed_at)
              VALUES
                (@DocId, @SigId, @Action, @ActorEid, @ActorUserType, @ActorName,
                 @SignatorySnapshot, @LocationSnapshot, GETDATE());",
            new
            {
                DocId = docId,
                SigId = (object?)sigId ?? DBNull.Value,
                Action = action,
                ActorEid = actorEid.ToString(),
                ActorUserType = actorUserType.ToString(),
                ActorName = (object?)actorName ?? DBNull.Value,
                SignatorySnapshot = signatoryRow != null
                    ? System.Text.Json.JsonSerializer.Serialize(signatoryRow)
                    : (object)DBNull.Value,
                LocationSnapshot = (object?)locationSnapshot ?? DBNull.Value
            },
            CommandType.Text,
            tx);
    }
```

Note: `QueryAsync` has no transaction-taking overload in `IDatabaseService`
(only `ExecuteAsync`/`ExecuteScalarAsync`/`QueryFirstOrDefaultAsync` do) —
the location-rows read above deliberately runs outside the transaction.
That's safe here because this feature never mutates
`document_signature_location` (see Global Constraints), so there's nothing
for it to see a dirty/uncommitted version of.

- [ ] **Step 3: Replace `AdminUpdateSignatoriesAsync`**

Replace the entire existing method (the one starting `// Admin-only
signatory edit — only ever deletes/reinserts sig_status = 0` through its
closing brace, currently `SigningService.cs:2663-2744`) with:

```csharp
    // Admin-only signatory edit — full per-row diff against the submitted
    // list. See docs/superpowers/specs/2026-09-23-admin-signatory-audit-design.md.
    public async Task<AdminUpdateSignatoriesResult> AdminUpdateSignatoriesAsync(
        AdminUpdateSignatoriesRequest request, int actorEid, int actorUserType)
    {
        try
        {
            var doc = await _dbService.QueryFirstOrDefaultAsync<DocumentAttachDetails, dynamic>(
                "SELECT doc_id AS DocId, doc_code AS DocCode FROM bacpdfsign.dbo.document_attach WHERE doc_id = @DocId",
                new { request.DocId },
                CommandType.Text);
            if (doc == null)
                return new AdminUpdateSignatoriesResult { Success = false, Message = "Document not found." };

            var actorName = await _dbService.QueryFirstOrDefaultAsync<string, dynamic>(
                "SELECT TOP 1 fname FROM bacpdfsign.dbo.signatory_names WHERE eid = @Eid AND user_type = @UserType",
                new { Eid = actorEid, UserType = actorUserType },
                CommandType.Text);

            var currentRows = (await _dbService.QueryAsync<DocumentSignatoryFullRowDto, dynamic>(
                @"SELECT sig_id AS SigId, doc_id AS DocId, sig_code AS SigCode, sig_eid AS SigEid,
                         sig_user_type AS SigUserType, sig_status AS SigStatus, sig_order AS SigOrder,
                         sig_remarks AS SigRemarks, sig_query_signed AS SigQuerySigned, sig_query_return AS SigQueryReturn,
                         sig_level AS SigLevel, sig_sign_count AS SigSignCount,
                         sig_remarks_datenTime AS SigRemarksDatenTime, date_time_inserted AS DateTimeInserted
                  FROM bacpdfsign.dbo.document_signatories
                  WHERE doc_id = @DocId",
                new { request.DocId },
                CommandType.Text)).ToDictionary(r => r.SigId);

            var submittedSigIds = request.Signatories.Where(s => s.SigId.HasValue).Select(s => s.SigId!.Value).ToHashSet();
            var toDelete = currentRows.Keys.Where(sigId => !submittedSigIds.Contains(sigId)).ToList();

            var tx = await _dbService.BeginTransactionAsync();
            try
            {
                foreach (var sigId in toDelete)
                {
                    var row = currentRows[sigId];
                    await WriteSignatoryAuditAsync(tx, request.DocId, sigId, "delete",
                        actorEid, actorUserType, actorName, row, includeLocation: row.SigStatus == 1);
                    await _dbService.ExecuteAsync<dynamic>(
                        "DELETE FROM bacpdfsign.dbo.document_signatories WHERE sig_id = @SigId",
                        new { SigId = sigId }, CommandType.Text, tx);
                }

                foreach (var sig in request.Signatories)
                {
                    if (sig.SigId.HasValue && currentRows.TryGetValue(sig.SigId.Value, out var existing))
                    {
                        var statusChanged = existing.SigStatus != sig.Status;
                        var orderChanged = existing.SigOrder != sig.Order;
                        var countChanged = existing.SigSignCount != sig.NumSignatures;
                        if (!statusChanged && !orderChanged && !countChanged)
                            continue;

                        var wasSigned = existing.SigStatus == 1;
                        if (statusChanged)
                            await WriteSignatoryAuditAsync(tx, request.DocId, sig.SigId, "status_change",
                                actorEid, actorUserType, actorName, existing, includeLocation: wasSigned);
                        if (orderChanged)
                            await WriteSignatoryAuditAsync(tx, request.DocId, sig.SigId, "order_change",
                                actorEid, actorUserType, actorName, existing, includeLocation: wasSigned);

                        await _dbService.ExecuteAsync<dynamic>(
                            @"UPDATE bacpdfsign.dbo.document_signatories
                              SET sig_status = @Status, sig_order = @Order, sig_sign_count = @SigSignCount
                              WHERE sig_id = @SigId",
                            new { sig.Status, sig.Order, SigSignCount = sig.NumSignatures, SigId = sig.SigId },
                            CommandType.Text, tx);
                    }
                    else
                    {
                        var insertedId = await _dbService.ExecuteScalarAsync<int, dynamic>(
                            @"INSERT INTO bacpdfsign.dbo.document_signatories
                                (doc_id, sig_code, sig_eid, sig_status, sig_order, sig_remarks,
                                 sig_query_signed, sig_query_return, sig_user_type, sig_level,
                                 sig_sign_count, sig_remarks_datenTime, date_time_inserted)
                              VALUES
                                (@DocId, @SigCode, @SigEid, @Status, @SigOrder, '',
                                 @QuerySigned, @QueryReturn, @SigUserType, @SigLevel,
                                 @SigSignCount, '', CONVERT(NVARCHAR(50), GETDATE(), 100));
                              SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new
                            {
                                request.DocId,
                                SigCode = doc.DocCode,
                                SigEid = sig.Eid,
                                sig.Status,
                                SigOrder = sig.Order,
                                QuerySigned = (object?)sig.QuerySigned ?? DBNull.Value,
                                QueryReturn = (object?)sig.QueryReturn ?? DBNull.Value,
                                SigUserType = sig.UserType,
                                SigLevel = NormalizeSigLevel(sig.Level),
                                SigSignCount = sig.NumSignatures > 0 ? sig.NumSignatures : 1
                            },
                            CommandType.Text, tx);

                        var inserted = new DocumentSignatoryFullRowDto
                        {
                            SigId = insertedId,
                            DocId = request.DocId,
                            SigCode = doc.DocCode,
                            SigEid = sig.Eid,
                            SigUserType = sig.UserType,
                            SigStatus = sig.Status,
                            SigOrder = sig.Order,
                            SigLevel = NormalizeSigLevel(sig.Level),
                            SigSignCount = sig.NumSignatures > 0 ? sig.NumSignatures : 1
                        };
                        await WriteSignatoryAuditAsync(tx, request.DocId, insertedId, "add",
                            actorEid, actorUserType, actorName, inserted, includeLocation: false);
                    }
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
```

- [ ] **Step 4: Manual verification**

This needs Task 5 (controller wiring) done first to be callable end to
end — for now, just confirm it builds:

Run: `dotnet build DigitalSignature/DigitalSignature.csproj`
Expected: errors only in `DGSignController.cs` (still calling the old
2-argument overload) and `AdminUpdateSignatoryStatusAsync` (Task 4) — no
errors inside `SigningService.cs` itself.

- [ ] **Step 5: Commit**

```bash
git add "DigitalSignature/Services/ISigningService.cs" "DigitalSignature/Services/SigningService.cs"
git commit -m "Rewrite AdminUpdateSignatoriesAsync as a per-row diff with audit writes"
```

---

## Task 4: Audit write for `AdminUpdateSignatoryStatusAsync`

**Files:**
- Modify: `DigitalSignature/Services/ISigningService.cs`
- Modify: `DigitalSignature/Services/SigningService.cs`

**Interfaces:**
- Consumes: `WriteSignatoryAuditAsync` (Task 3).
- Produces: `Task<AdminUpdateSignatoryStatusResult> AdminUpdateSignatoryStatusAsync(AdminUpdateSignatoryStatusRequest request, int actorEid, int actorUserType)` — consumed by Task 5.

- [ ] **Step 1: Update the interface signature**

In `ISigningService.cs`, replace:

```csharp
    Task<AdminUpdateSignatoryStatusResult> AdminUpdateSignatoryStatusAsync(AdminUpdateSignatoryStatusRequest request);
```

with:

```csharp
    Task<AdminUpdateSignatoryStatusResult> AdminUpdateSignatoryStatusAsync(AdminUpdateSignatoryStatusRequest request, int actorEid, int actorUserType);
```

- [ ] **Step 2: Rewrite the method body**

In `SigningService.cs`, replace the existing
`AdminUpdateSignatoryStatusAsync` method with:

```csharp
    public async Task<AdminUpdateSignatoryStatusResult> AdminUpdateSignatoryStatusAsync(
        AdminUpdateSignatoryStatusRequest request, int actorEid, int actorUserType)
    {
        try
        {
            var existing = await _dbService.QueryFirstOrDefaultAsync<DocumentSignatoryFullRowDto, dynamic>(
                @"SELECT sig_id AS SigId, doc_id AS DocId, sig_code AS SigCode, sig_eid AS SigEid,
                         sig_user_type AS SigUserType, sig_status AS SigStatus, sig_order AS SigOrder,
                         sig_remarks AS SigRemarks, sig_query_signed AS SigQuerySigned, sig_query_return AS SigQueryReturn,
                         sig_level AS SigLevel, sig_sign_count AS SigSignCount,
                         sig_remarks_datenTime AS SigRemarksDatenTime, date_time_inserted AS DateTimeInserted
                  FROM bacpdfsign.dbo.document_signatories
                  WHERE sig_id = @SigId",
                new { request.SigId },
                CommandType.Text);
            if (existing == null)
                return new AdminUpdateSignatoryStatusResult { Success = false, Message = "Signatory row not found." };

            var actorName = await _dbService.QueryFirstOrDefaultAsync<string, dynamic>(
                "SELECT TOP 1 fname FROM bacpdfsign.dbo.signatory_names WHERE eid = @Eid AND user_type = @UserType",
                new { Eid = actorEid, UserType = actorUserType },
                CommandType.Text);

            var tx = await _dbService.BeginTransactionAsync();
            try
            {
                await WriteSignatoryAuditAsync(tx, existing.DocId, existing.SigId, "status_change",
                    actorEid, actorUserType, actorName, existing, includeLocation: existing.SigStatus == 1);

                var affected = await _dbService.ExecuteAsync<dynamic>(
                    "UPDATE bacpdfsign.dbo.document_signatories SET sig_status = @Status WHERE sig_id = @SigId",
                    new { request.SigId, request.Status },
                    CommandType.Text, tx);

                if (affected == 0)
                {
                    await _dbService.RollbackTransactionAsync(tx);
                    return new AdminUpdateSignatoryStatusResult { Success = false, Message = "Signatory row not found." };
                }

                await _dbService.CommitTransactionAsync(tx);
                return new AdminUpdateSignatoryStatusResult { Success = true };
            }
            catch
            {
                try { await _dbService.RollbackTransactionAsync(tx); } catch { /* already rolled back or connection gone */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed admin status override for sig {SigId}", request.SigId);
            return new AdminUpdateSignatoryStatusResult { Success = false, IsServerError = true, Message = "Something went wrong" };
        }
    }
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build DigitalSignature/DigitalSignature.csproj`
Expected: remaining errors only in `DGSignController.cs` (Task 5 fixes
these).

- [ ] **Step 4: Commit**

```bash
git add "DigitalSignature/Services/ISigningService.cs" "DigitalSignature/Services/SigningService.cs"
git commit -m "Add audit write to AdminUpdateSignatoryStatusAsync"
```

---

## Task 5: `GetAdminSignatoryAuditAsync` + controller wiring

**Files:**
- Modify: `DigitalSignature/Services/ISigningService.cs` (already declared in Task 3, Step 1)
- Modify: `DigitalSignature/Services/SigningService.cs`
- Modify: `DigitalSignature/Controllers/DGSignController.cs`

**Interfaces:**
- Consumes: `AdminSignatoryAuditDto` (Task 2), `AdminUpdateSignatoriesAsync`/`AdminUpdateSignatoryStatusAsync` new signatures (Tasks 3-4).
- Produces: `GET DGSign/admin_get_signatory_audit?docId=` — consumed by the frontend History panel (Task 10).

- [ ] **Step 1: Implement `GetAdminSignatoryAuditAsync`**

In `SigningService.cs`, add (near `AdminUpdateSignatoriesAsync`):

```csharp
    public async Task<List<AdminSignatoryAuditDto>> GetAdminSignatoryAuditAsync(int docId)
    {
        var rows = await _dbService.QueryAsync<AdminSignatoryAuditDto, dynamic>(
            @"SELECT id AS Id, doc_id AS DocId, sig_id AS SigId, action AS Action,
                     changed_by_eid AS ChangedByEid, changed_by_user_type AS ChangedByUserType,
                     changed_by_name AS ChangedByName, signatory_snapshot AS SignatorySnapshot,
                     location_snapshot AS LocationSnapshot, changed_at AS ChangedAt
              FROM bacpdfsign.dbo.admin_signatory_audit
              WHERE doc_id = @DocId
              ORDER BY changed_at DESC",
            new { DocId = docId },
            CommandType.Text);
        return rows.ToList();
    }
```

- [ ] **Step 2: Add a claims-reading helper and wire the two existing endpoints**

In `DGSignController.cs`, add a small private helper near
`IsCurrentUserAdminAsync` (around line 1810):

```csharp
        // Parses the caller's own eid/user_type from JWT claims — same
        // source IsCurrentUserAdminAsync trusts. Used to attribute
        // admin_signatory_audit rows to the actual acting admin.
        private (int Eid, int UserType) GetCurrentActor()
        {
            var eidClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userTypeClaim = User.FindFirstValue("UserType");
            int.TryParse(eidClaim, out var eid);
            int.TryParse(userTypeClaim, out var userType);
            return (eid, userType);
        }
```

Then update the two call sites (around line 720-750):

```csharp
        [HttpPost("admin_update_signatories")]
        public async Task<IActionResult> AdminUpdateSignatories([FromBody] AdminUpdateSignatoriesRequest request)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var actor = GetCurrentActor();
            var result = await _signingService.AdminUpdateSignatoriesAsync(request, actor.Eid, actor.UserType);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new { success = true, signatoryCount = result.SignatoryCount });
        }

        [HttpPost("admin_update_signatory_status")]
        public async Task<IActionResult> AdminUpdateSignatoryStatus([FromBody] AdminUpdateSignatoryStatusRequest request)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var actor = GetCurrentActor();
            var result = await _signingService.AdminUpdateSignatoryStatusAsync(request, actor.Eid, actor.UserType);
            if (!result.Success)
            {
                return result.IsServerError
                    ? StatusCode((int)HttpStatusCode.InternalServerError, new { success = false, message = result.Message })
                    : BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new { success = true });
        }
```

(Keep whatever the existing tail of `AdminUpdateSignatoryStatus` already
returns on success — just add the `actor` line and pass it through; don't
change the response shape.)

- [ ] **Step 3: Add the new read endpoint**

Add directly after `AdminUpdateSignatoryStatus`:

```csharp
        // Read-only history for the Document Search dialog's audit panel —
        // every admin-initiated signatory change on this document.
        [HttpGet("admin_get_signatory_audit")]
        public async Task<IActionResult> AdminGetSignatoryAudit([FromQuery] int docId)
        {
            if (!await IsCurrentUserAdminAsync())
                return Forbid();

            var rows = await _signingService.GetAdminSignatoryAuditAsync(docId);
            return Ok(rows);
        }
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build DigitalSignature/DigitalSignature.csproj`
Expected: build succeeds, no errors.

- [ ] **Step 5: Manual end-to-end verification**

With the app running (per this repo's own publish/run workflow — not
started by this task), as an admin:

1. `POST DGSign/admin_update_signatory_status` with a real `SigId` and a
   `Status` different from its current value.
   Expected: `{ "success": true }`, and
   `SELECT * FROM bacpdfsign.dbo.admin_signatory_audit WHERE sig_id =
   <that id> ORDER BY changed_at DESC` shows one new row with
   `action = 'status_change'` and a non-null `changed_by_eid`.
2. `GET DGSign/admin_get_signatory_audit?docId=<that doc's id>`.
   Expected: includes the row from step 1.

- [ ] **Step 6: Commit**

```bash
git add "DigitalSignature/Services/SigningService.cs" "DigitalSignature/Controllers/DGSignController.cs"
git commit -m "Wire admin signatory audit into controller, add history read endpoint"
```

---

## Task 6: Doc Code read-only

**Files:**
- Modify: `src/routes/_authenticated/admin/document-search.tsx`

**Interfaces:**
- Consumes: existing `docCode` state in `AdminDocumentViewDialog`.
- Produces: nothing new (leaf UI change).

- [ ] **Step 1: Make the field read-only**

Find (currently around line 907-910):

```tsx
                                <div className="space-y-1.5">
                                    <Label>Doc Code</Label>
                                    <Input value={docCode} onChange={(e) => setDocCode(e.target.value)} />
                                </div>
```

Replace with:

```tsx
                                <div className="space-y-1.5">
                                    <Label>Doc Code</Label>
                                    <Input value={docCode} disabled />
                                </div>
```

(`setDocCode` stays declared/used elsewhere — it's still set from the
loaded document in the existing `useEffect`; only the input's editability
changes. If `setDocCode`'s setter becomes otherwise unused after this,
TypeScript/lint will flag it — it won't, since the load effect still calls
it.)

- [ ] **Step 2: Verify**

Run: `npx tsc --noEmit -p tsconfig.json`
Expected: no new errors.

- [ ] **Step 3: Commit**

```bash
git add "src/routes/_authenticated/admin/document-search.tsx"
git commit -m "Make Doc Code read-only in admin document dialog"
```

---

## Task 7: Merge signed + pending signatory rows into one editable table

**Files:**
- Modify: `src/routes/_authenticated/admin/document-search.tsx`

**Interfaces:**
- Consumes: `get_document_for_edit` response shape (`data.signatories`, unchanged — already returns every row with `sigId, fname, sigOrder, sigLevel, sigEid, sigUserType, sigSignCount, sigStatus`).
- Produces: a single `SignatoryRow[]` state (`rows`) replacing `signedRows`/`pendingRows`, consumed by Task 8 (status editing) and Task 9 (save wiring).

- [ ] **Step 1: Replace the two row types with one**

Find (currently around line 111-128):

```tsx
interface PendingSignatoryRow {
    id: string | number
    name: string
    abbrValue?: string
    eid: number
    userType: number
    numSignatures: number
    order: number
    level: number
}

interface SignedSignatoryRow {
    sigId: number
    name: string
    order: number
    level: number
    statusId: number
}
```

Replace with:

```tsx
interface SignatoryRow {
    id: string | number      // React key: numeric sigId for existing rows, "new-<eid>" for unsaved additions
    sigId: number | null     // null until this row is saved
    name: string
    abbrValue?: string
    eid: number
    userType: number
    statusId: number
    numSignatures: number
    order: number
    level: number
}
```

- [ ] **Step 2: Replace the state and load effect**

Find (currently around line 591-593):

```tsx
    const [signedRows, setSignedRows] = React.useState<SignedSignatoryRow[]>([])
    const [pendingRows, setPendingRows] = React.useState<PendingSignatoryRow[]>([])
    const [selectedIds, setSelectedIds] = React.useState<(string | number)[]>([])
```

Replace with:

```tsx
    const [rows, setRows] = React.useState<SignatoryRow[]>([])
    const [selectedIds, setSelectedIds] = React.useState<(string | number)[]>([])
```

Find, inside the `get_document_for_edit` load effect (currently around
line 630-653):

```tsx
                    const allSigs = data.signatories ?? []
                    const signed = allSigs
                        .filter((s: any) => (s.sigStatus ?? 0) !== 0)
                        .map((s: any) => ({
                            sigId: s.sigId,
                            name: s.fname || String(s.sigEid),
                            order: s.sigOrder,
                            level: s.sigLevel,
                            statusId: s.sigStatus ?? 0,
                        }))
                    const pending: PendingSignatoryRow[] = allSigs
                        .filter((s: any) => (s.sigStatus ?? 0) === 0)
                        .map((s: any) => ({
                            id: s.sigId,
                            name: s.fname || String(s.sigEid),
                            eid: s.sigEid,
                            userType: s.sigUserType,
                            numSignatures: s.sigSignCount ?? 1,
                            order: s.sigOrder ?? 1,
                            level: s.sigLevel ?? 1,
                        }))
                    setSignedRows(signed)
                    setPendingRows(pending)
                    setSelectedIds(pending.map((p) => p.id))
```

Replace with:

```tsx
                    const allSigs = data.signatories ?? []
                    const mapped: SignatoryRow[] = allSigs.map((s: any) => ({
                        id: s.sigId,
                        sigId: s.sigId,
                        name: s.fname || String(s.sigEid),
                        eid: s.sigEid,
                        userType: s.sigUserType,
                        statusId: s.sigStatus ?? 0,
                        numSignatures: s.sigSignCount ?? 1,
                        order: s.sigOrder ?? 1,
                        level: s.sigLevel ?? 1,
                    }))
                    setRows(mapped)
                    setSelectedIds(mapped.map((r) => r.id))
```

- [ ] **Step 3: Update the add/remove/order/count handlers**

Find (currently around line 694-727):

```tsx
    const handleSignatoryChange = (ids: (string | number)[], items: DynamicMultiModel[]) => {
        setSelectedIds(ids)
        setPendingRows((prev) => {
            const kept = prev.filter((row) => ids.some((id) => String(id) === String(row.id)))
            const keptIds = new Set(kept.map((r) => String(r.id)))
            let nextOrder = kept.length ? Math.max(...kept.map((r) => r.order)) + 1 : 1
            const added = items
                .filter((item) => !keptIds.has(String(item.id)))
                .map((item) => ({
                    id: `new-${item.id}`,
                    name: item.value,
                    abbrValue: item.abbr_value,
                    eid: Number(item.additional_id ?? 0),
                    userType: Number((item as Record<string, unknown>)["additional_Id_two"] ?? 0),
                    numSignatures: 1,
                    order: nextOrder++,
                    level: 1,
                }))
            return [...kept, ...added]
        })
    }

    const removePending = (id: string | number) => {
        setSelectedIds((prev) => prev.filter((s) => String(s) !== String(id)))
        setPendingRows((prev) => prev.filter((row) => String(row.id) !== String(id)))
    }

    const updatePendingOrder = (id: string | number, order: number) => {
        setPendingRows((prev) => prev.map((row) => (String(row.id) === String(id) ? { ...row, order } : row)))
    }

    const updatePendingCount = (id: string | number, numSignatures: number) => {
        setPendingRows((prev) => prev.map((row) => (String(row.id) === String(id) ? { ...row, numSignatures } : row)))
    }
```

Replace with:

```tsx
    const handleSignatoryChange = (ids: (string | number)[], items: DynamicMultiModel[]) => {
        setSelectedIds(ids)
        setRows((prev) => {
            const kept = prev.filter((row) => ids.some((id) => String(id) === String(row.id)))
            const keptIds = new Set(kept.map((r) => String(r.id)))
            let nextOrder = kept.length ? Math.max(...kept.map((r) => r.order)) + 1 : 1
            const added = items
                .filter((item) => !keptIds.has(String(item.id)))
                .map((item) => ({
                    id: `new-${item.id}`,
                    sigId: null,
                    name: item.value,
                    abbrValue: item.abbr_value,
                    eid: Number(item.additional_id ?? 0),
                    userType: Number((item as Record<string, unknown>)["additional_Id_two"] ?? 0),
                    statusId: 0,
                    numSignatures: 1,
                    order: nextOrder++,
                    level: 1,
                }))
            return [...kept, ...added]
        })
    }

    const removeRow = (id: string | number) => {
        setSelectedIds((prev) => prev.filter((s) => String(s) !== String(id)))
        setRows((prev) => prev.filter((row) => String(row.id) !== String(id)))
    }

    const updateRowOrder = (id: string | number, order: number) => {
        setRows((prev) => prev.map((row) => (String(row.id) === String(id) ? { ...row, order } : row)))
    }

    const updateRowCount = (id: string | number, numSignatures: number) => {
        setRows((prev) => prev.map((row) => (String(row.id) === String(id) ? { ...row, numSignatures } : row)))
    }
```

- [ ] **Step 4: Verify**

Run: `npx tsc --noEmit -p tsconfig.json`
Expected: errors only where `signedRows`/`pendingRows`/`removePending`/
`updatePendingOrder`/`updatePendingCount`/`handleSignedStatusChange` are
still referenced in the JSX (Task 8 replaces those).

- [ ] **Step 5: Commit**

Don't commit yet — Task 7's state changes leave the JSX referencing
removed identifiers, so the file won't build standalone. Task 8 finishes
the JSX and both get committed together at the end of Task 8.

---

## Task 8: One merged signatories table + status editing for every row

**Files:**
- Modify: `src/routes/_authenticated/admin/document-search.tsx`

**Interfaces:**
- Consumes: `rows`, `removeRow`, `updateRowOrder`, `updateRowCount`, `handleSignatoryChange` (Task 7); `sigStatusOptions` (existing state, unchanged).
- Produces: `updateRowStatus(id, statusId)` (local-only, for unsaved rows) and `handleRowStatusChange(sigId, newStatusId)` (immediate PATCH, for saved rows) — both used only within this task's JSX.

- [ ] **Step 1: Replace `handleSignedStatusChange` with a version that works on any row**

Find (currently around line 729-747):

```tsx
    const handleSignedStatusChange = async (sigId: number, newStatusId: number) => {
        setUpdatingSigId(sigId)
        try {
            const res = await authFetch(apiUrl("DGSign/admin_update_signatory_status"), {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ SigId: sigId, Status: newStatusId }),
            })
            const data = await res.json().catch(() => null)
            if (!res.ok || !data?.success) throw new Error(data?.message ?? "Failed to update signatory status")

            setSignedRows((prev) => prev.map((r) => (r.sigId === sigId ? { ...r, statusId: newStatusId } : r)))
            toast.success("Signatory status updated.")
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Failed to update signatory status.")
        } finally {
            setUpdatingSigId(null)
        }
    }
```

Replace with:

```tsx
    // Status is applied immediately (this row already exists server-side);
    // order/count/delete/add stay local and go through the batch Save
    // below, same as before.
    const handleRowStatusChange = async (sigId: number, newStatusId: number) => {
        setUpdatingSigId(sigId)
        try {
            const res = await authFetch(apiUrl("DGSign/admin_update_signatory_status"), {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ SigId: sigId, Status: newStatusId }),
            })
            const data = await res.json().catch(() => null)
            if (!res.ok || !data?.success) throw new Error(data?.message ?? "Failed to update signatory status")

            setRows((prev) => prev.map((r) => (r.sigId === sigId ? { ...r, statusId: newStatusId } : r)))
            toast.success("Signatory status updated.")
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Failed to update signatory status.")
        } finally {
            setUpdatingSigId(null)
        }
    }

    // A brand-new row (no sigId yet) has nothing to PATCH — its status is
    // just local state until the batch Save inserts it.
    const updateRowStatus = (id: string | number, statusId: number) => {
        setRows((prev) => prev.map((row) => (String(row.id) === String(id) ? { ...row, statusId } : row)))
    }
```

- [ ] **Step 2: Replace the two-block signatories JSX with one table**

Find the whole block from the `<Label>Signatories</Label>` line through
the closing of the pending-rows table (currently roughly lines 977-1024:
from `<div className="space-y-3">` / `<Label>Signatories</Label>` down
through the `pendingRows.length > 0 && (...)` block's closing `)}`, but
**not** past it — leave the `</div>` that closes the outer
`space-y-3` container and everything after it alone for now):

```tsx
                                <div className="space-y-3">
                                    <Label>Signatories</Label>

                                    {signedRows.length > 0 && (
                                        <div className="rounded-lg border divide-y bg-muted/20">
                                            {signedRows.map((row) => (
                                                <div key={row.sigId} className="flex items-center justify-between gap-2 px-3 py-2 text-sm">
                                                    <span className="flex items-center gap-1.5 text-muted-foreground truncate">
                                                        <Lock className="h-3 w-3 shrink-0" />
                                                        <span className="truncate">{row.name} — order {row.order}</span>
                                                    </span>
                                                    <Select
                                                        value={String(row.statusId)}
                                                        onValueChange={(v) => handleSignedStatusChange(row.sigId, Number(v))}
                                                        disabled={updatingSigId === row.sigId}
                                                    >
                                                        <SelectTrigger className="h-7 w-32 text-xs shrink-0">
                                                            {updatingSigId === row.sigId ? (
                                                                <Loader2 className="h-3 w-3 animate-spin" />
                                                            ) : (
                                                                <SelectValue />
                                                            )}
                                                        </SelectTrigger>
                                                        <SelectContent>
                                                            {sigStatusOptions.map((s) => (
                                                                <SelectItem key={s.id} value={String(s.id)} className="text-xs">
                                                                    {s.statusType}
                                                                </SelectItem>
                                                            ))}
                                                        </SelectContent>
                                                    </Select>
                                                </div>
                                            ))}
                                        </div>
                                    )}

                                    <DynamicMultiSelect
                                        api={apiUrl("references/get_listofSignatories")}
                                        placeholder="Add pending signatories..."
                                        value={selectedIds}
                                        maxBadges={2}
                                        showSelectAll={false}
                                        displayRenderer={(item) => {
                                            const tag = item["additional_Id_two"] === "0" ? "PGAS" : "Non-PGAS"
                                            return `${item.value} (${item.abbr_value ?? ""}) (${tag})`
                                        }}
                                        onChangeCallback={handleSignatoryChange}
                                    />

                                    {pendingRows.length > 0 && (
                                        <div className="rounded-lg border overflow-hidden">
                                            <div className="overflow-x-auto">
                                                <Table>
                                                    <TableHeader>
                                                        <TableRow className="bg-muted/40 hover:bg-muted/40">
                                                            <TableHead>Name</TableHead>
                                                            <TableHead className="text-center w-20">Count</TableHead>
                                                            <TableHead className="text-center w-20">Order</TableHead>
                                                            <TableHead className="text-center w-14">Remove</TableHead>
                                                        </TableRow>
                                                    </TableHeader>
                                                    <TableBody>
                                                        {pendingRows.map((row) => (
                                                            <TableRow key={row.id}>
                                                                <TableCell className="text-sm">{row.name}</TableCell>
                                                                <TableCell className="text-center">
                                                                    <Input
                                                                        type="number"
                                                                        min={1}
                                                                        value={row.numSignatures}
                                                                        onChange={(e) => updatePendingCount(row.id, Math.max(1, Number(e.target.value) || 1))}
                                                                        className="h-8 w-14 text-center mx-auto"
                                                                    />
                                                                </TableCell>
                                                                <TableCell className="text-center">
                                                                    <Input
                                                                        type="number"
                                                                        min={1}
                                                                        value={row.order}
                                                                        onChange={(e) => updatePendingOrder(row.id, Math.max(1, Number(e.target.value) || 1))}
                                                                        className="h-8 w-14 text-center mx-auto"
                                                                    />
                                                                </TableCell>
                                                                <TableCell className="text-center">
                                                                    <Button type="button" variant="ghost" size="icon" className="h-8 w-8" onClick={() => removePending(row.id)}>
                                                                        <Trash2 className="h-3.5 w-3.5 text-destructive" />
                                                                    </Button>
                                                                </TableCell>
                                                            </TableRow>
                                                        ))}
                                                    </TableBody>
                                                </Table>
                                            </div>
                                        </div>
                                    )}
                                </div>
```

Replace with:

```tsx
                                <div className="space-y-3">
                                    <Label>Signatories</Label>

                                    <DynamicMultiSelect
                                        api={apiUrl("references/get_listofSignatories")}
                                        placeholder="Add signatories..."
                                        value={selectedIds}
                                        maxBadges={2}
                                        showSelectAll={false}
                                        displayRenderer={(item) => {
                                            const tag = item["additional_Id_two"] === "0" ? "PGAS" : "Non-PGAS"
                                            return `${item.value} (${item.abbr_value ?? ""}) (${tag})`
                                        }}
                                        onChangeCallback={handleSignatoryChange}
                                    />

                                    {rows.length > 0 && (
                                        <div className="rounded-lg border overflow-hidden">
                                            <div className="overflow-x-auto">
                                                <Table>
                                                    <TableHeader>
                                                        <TableRow className="bg-muted/40 hover:bg-muted/40">
                                                            <TableHead>Name</TableHead>
                                                            <TableHead className="w-36">Status</TableHead>
                                                            <TableHead className="text-center w-20">Count</TableHead>
                                                            <TableHead className="text-center w-20">Order</TableHead>
                                                            <TableHead className="text-center w-14">Remove</TableHead>
                                                        </TableRow>
                                                    </TableHeader>
                                                    <TableBody>
                                                        {rows.map((row) => (
                                                            <TableRow key={row.id}>
                                                                <TableCell className="text-sm">{row.name}</TableCell>
                                                                <TableCell>
                                                                    <Select
                                                                        value={String(row.statusId)}
                                                                        onValueChange={(v) =>
                                                                            row.sigId != null
                                                                                ? handleRowStatusChange(row.sigId, Number(v))
                                                                                : updateRowStatus(row.id, Number(v))
                                                                        }
                                                                        disabled={row.sigId != null && updatingSigId === row.sigId}
                                                                    >
                                                                        <SelectTrigger className="h-8 w-full text-xs">
                                                                            {row.sigId != null && updatingSigId === row.sigId ? (
                                                                                <Loader2 className="h-3 w-3 animate-spin" />
                                                                            ) : (
                                                                                <SelectValue />
                                                                            )}
                                                                        </SelectTrigger>
                                                                        <SelectContent>
                                                                            {sigStatusOptions.map((s) => (
                                                                                <SelectItem key={s.id} value={String(s.id)} className="text-xs">
                                                                                    {s.statusType}
                                                                                </SelectItem>
                                                                            ))}
                                                                        </SelectContent>
                                                                    </Select>
                                                                </TableCell>
                                                                <TableCell className="text-center">
                                                                    <Input
                                                                        type="number"
                                                                        min={1}
                                                                        value={row.numSignatures}
                                                                        onChange={(e) => updateRowCount(row.id, Math.max(1, Number(e.target.value) || 1))}
                                                                        className="h-8 w-14 text-center mx-auto"
                                                                    />
                                                                </TableCell>
                                                                <TableCell className="text-center">
                                                                    <Input
                                                                        type="number"
                                                                        min={1}
                                                                        value={row.order}
                                                                        onChange={(e) => updateRowOrder(row.id, Math.max(1, Number(e.target.value) || 1))}
                                                                        className="h-8 w-14 text-center mx-auto"
                                                                    />
                                                                </TableCell>
                                                                <TableCell className="text-center">
                                                                    <Button type="button" variant="ghost" size="icon" className="h-8 w-8" onClick={() => removeRow(row.id)}>
                                                                        <Trash2 className="h-3.5 w-3.5 text-destructive" />
                                                                    </Button>
                                                                </TableCell>
                                                            </TableRow>
                                                        ))}
                                                    </TableBody>
                                                </Table>
                                            </div>
                                        </div>
                                    )}
                                </div>
```

Note `Lock` may now be unused in this file if it wasn't used elsewhere —
check with a search before removing its import; if unused, drop it from
the `lucide-react` import list at the top of the file.

- [ ] **Step 3: Verify**

Run: `npx tsc --noEmit -p tsconfig.json`
Expected: errors only in `handleSave` (Task 9 fixes the request body
that still references `pendingRows`).

- [ ] **Step 4: Commit**

```bash
git add "src/routes/_authenticated/admin/document-search.tsx"
git commit -m "Merge signed/pending signatory rows into one editable table"
```

---

## Task 9: Wire `handleSave` to the merged row list

**Files:**
- Modify: `src/routes/_authenticated/admin/document-search.tsx`

**Interfaces:**
- Consumes: `rows` (Task 7), backend `AdminSignatoryItemDto` shape (Task 2: `SigId, Eid, UserType, Order, NumSignatures, Level, Status`).
- Produces: nothing new — closes out the save flow.

- [ ] **Step 1: Update the signatories portion of `handleSave`**

Find (currently around line 789-804):

```tsx
            const sigRes = await authFetch(apiUrl("DGSign/admin_update_signatories"), {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    DocId: doc.docId,
                    Signatories: pendingRows.map((r) => ({
                        Eid: r.eid,
                        UserType: r.userType,
                        Order: r.order,
                        NumSignatures: r.numSignatures,
                        Level: r.level,
                    })),
                }),
            })
```

Replace with:

```tsx
            const sigRes = await authFetch(apiUrl("DGSign/admin_update_signatories"), {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    DocId: doc.docId,
                    Signatories: rows.map((r) => ({
                        SigId: r.sigId,
                        Eid: r.eid,
                        UserType: r.userType,
                        Order: r.order,
                        NumSignatures: r.numSignatures,
                        Level: r.level,
                        Status: r.statusId,
                    })),
                }),
            })
```

- [ ] **Step 2: Verify**

Run: `npx tsc --noEmit -p tsconfig.json`
Expected: no errors anywhere in `document-search.tsx`.

- [ ] **Step 3: Manual verification**

With the app running (per the user's own publish/run workflow):

1. Open a document with at least one already-signed signatory.
2. Change its status via the row's Select. Expected: instant toast
   "Signatory status updated.", no page reload needed, row reflects the
   new status.
3. Change its Order value, then click "Save Changes". Expected: success
   toast, dialog data reloads (`onSaved`/`onClose` → parent re-searches),
   reopening the document shows the new order.
4. On a document with 3+ signatories, note the `sig_order` of the two
   rows you are *not* about to touch, then delete a signed row via its
   trash icon and Save. Expected: succeeds;
   `SELECT * FROM bacpdfsign.dbo.document_signatories WHERE sig_id =
   <deleted id>` returns nothing; the untouched rows' `sig_order` values
   are unchanged (the diff never renumbers rows it didn't touch); `SELECT
   * FROM bacpdfsign.dbo.admin_signatory_audit WHERE sig_id = <deleted
   id>` shows an `action = 'delete'` row with a non-null
   `location_snapshot` (assuming that signatory actually had a location
   row) and a non-null `signatory_snapshot`.
5. Add a brand-new signatory via the multiselect, set its status/order,
   Save. Expected: succeeds; a new `document_signatories` row exists;
   `admin_signatory_audit` has an `action = 'add'` row for its new
   `sig_id`.
6. Re-open the same document and click "Save Changes" immediately with no
   edits. Expected: `admin_signatory_audit` gets no new rows (no-op diff).

- [ ] **Step 4: Commit**

```bash
git add "src/routes/_authenticated/admin/document-search.tsx"
git commit -m "Send merged signatory rows (status/sigId included) to admin_update_signatories"
```

---

## Task 10: History panel

**Files:**
- Modify: `src/routes/_authenticated/admin/document-search.tsx`

**Interfaces:**
- Consumes: `GET DGSign/admin_get_signatory_audit?docId=` (Task 5), `AdminSignatoryAuditDto` shape.
- Produces: nothing consumed elsewhere — leaf UI feature.

- [ ] **Step 1: Add the audit row type and fetch state**

Near the top of `AdminDocumentViewDialog` (after the existing `interface`
declarations, before the component), add:

```tsx
interface AdminSignatoryAuditRow {
    id: number
    docId: number
    sigId: number | null
    action: string
    changedByEid: string
    changedByName: string | null
    signatorySnapshot: string | null
    locationSnapshot: string | null
    changedAt: string
}
```

Inside `AdminDocumentViewDialog`, alongside the other `React.useState`
declarations (near `signedRows`/`pendingRows`, now `rows`), add:

```tsx
    const [auditRows, setAuditRows] = React.useState<AdminSignatoryAuditRow[]>([])
    const [auditLoading, setAuditLoading] = React.useState(false)
    const [auditError, setAuditError] = React.useState<string | null>(null)
    const [auditOpen, setAuditOpen] = React.useState(false)
```

- [ ] **Step 2: Fetch on dialog open**

Add a new effect near the existing `get_document_for_edit` effect:

```tsx
    React.useEffect(() => {
        if (!doc) {
            setAuditRows([])
            setAuditError(null)
            return
        }
        let cancelled = false
        setAuditLoading(true)
        setAuditError(null)
        authFetch(apiUrl(`DGSign/admin_get_signatory_audit?docId=${doc.docId}`))
            .then((res) => {
                if (!res.ok) throw new Error(`Failed to load history (${res.status})`)
                return res.json()
            })
            .then((data) => { if (!cancelled) setAuditRows(data ?? []) })
            .catch((err: unknown) => {
                if (!cancelled) setAuditError(err instanceof Error ? err.message : "Failed to load history")
            })
            .finally(() => { if (!cancelled) setAuditLoading(false) })
        return () => { cancelled = true }
    }, [doc])
```

- [ ] **Step 3: Render the panel**

Add this directly after the signatories `<div className="space-y-3">...
</div>` block from Task 8 (still inside the same left-column
`<div className="space-y-5">` wrapper):

```tsx
                                <Collapsible open={auditOpen} onOpenChange={setAuditOpen}>
                                    <CollapsibleTrigger className="flex w-full items-center gap-2 text-sm font-semibold">
                                        History
                                        <ChevronDown className="h-4 w-4 text-muted-foreground ml-auto transition-transform data-[state=open]:rotate-180" />
                                    </CollapsibleTrigger>
                                    <CollapsibleContent className="pt-2">
                                        {auditLoading ? (
                                            <Skeleton className="h-16 w-full" />
                                        ) : auditError ? (
                                            <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
                                                <span>{auditError}</span>
                                                <Button size="sm" variant="outline" onClick={() => setAuditOpen((o) => o)}>Retry</Button>
                                            </div>
                                        ) : auditRows.length === 0 ? (
                                            <p className="text-xs text-muted-foreground">No admin changes recorded for this document.</p>
                                        ) : (
                                            <div className="rounded-lg border divide-y max-h-64 overflow-y-auto">
                                                {auditRows.map((a) => (
                                                    <div key={a.id} className="px-3 py-2 text-xs space-y-0.5">
                                                        <div className="flex items-center justify-between">
                                                            <span className="font-medium">{a.action}</span>
                                                            <span className="text-muted-foreground">{a.changedAt}</span>
                                                        </div>
                                                        <div className="text-muted-foreground">
                                                            by {a.changedByName || a.changedByEid}
                                                            {a.sigId != null ? ` — sig #${a.sigId}` : ""}
                                                        </div>
                                                    </div>
                                                ))}
                                            </div>
                                        )}
                                    </CollapsibleContent>
                                </Collapsible>
```

(The Retry button intentionally just re-triggers the same `open` state —
since the fetch effect depends on `doc`, not `auditOpen`, a real retry
needs a token similar to `pdfReloadToken`. Add one: declare
`const [auditReloadToken, setAuditReloadToken] = React.useState(0)`
alongside the other audit state, add it to the fetch effect's dependency
array (`[doc, auditReloadToken]`), and change the Retry button's
`onClick` to `() => setAuditReloadToken((c) => c + 1)`.)

- [ ] **Step 4: Verify**

Run: `npx tsc --noEmit -p tsconfig.json`
Expected: no errors.

- [ ] **Step 5: Manual verification**

Open a document that already has audit rows from Task 9's manual testing.
Expected: History panel (collapsed by default) expands to show each
recorded action, newest first, with a readable admin name and timestamp.

- [ ] **Step 6: Commit**

```bash
git add "src/routes/_authenticated/admin/document-search.tsx"
git commit -m "Add signatory-change history panel to admin document dialog"
```

---

## Final check

- [ ] Re-read `docs/superpowers/specs/2026-09-23-admin-signatory-audit-design.md` top to bottom against the 10 tasks above — confirm every section (Data model, Backend changes, Frontend changes, Error handling) maps to at least one completed task.
- [ ] Run `dotnet build DigitalSignature/DigitalSignature.csproj` and `npx tsc --noEmit -p tsconfig.json` one more time from a clean `git status` — both should be silent.
- [ ] Report done to the user with your own publish workflow — this plan does not build or run either project for you (see `feedback_dont_build_api_locally.md`).
