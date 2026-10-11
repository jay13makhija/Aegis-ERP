-- Database-level guard for the audit log. Run once against Erp_Audit AFTER the first migration (or put it in a migration via migrationBuilder.Sql).
-- 1) Even a compromised application cannot rewrite history through UPDATE/DELETE:
CREATE OR ALTER TRIGGER dbo.trg_AuditLog_Immutable ON dbo.AuditLog INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51000, 'The audit log is append-only.', 1;
END;
GO
-- 2) Principle of least privilege: the audit service login should only be able to INSERT and SELECT.
-- DENY UPDATE, DELETE ON dbo.AuditLog TO [erp_audit_app];
