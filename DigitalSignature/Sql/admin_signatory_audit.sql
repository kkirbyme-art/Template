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
