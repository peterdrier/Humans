-- PostgreSQL trigger to prevent UPDATE and DELETE on audit_log table
-- This ensures the append-only nature of the audit trail

-- Function that raises an exception on UPDATE or DELETE
CREATE OR REPLACE FUNCTION prevent_audit_log_modification()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'UPDATE operations are not allowed on audit_log table. Audit log entries are immutable.';
    ELSIF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'DELETE operations are not allowed on audit_log table. Audit log entries are immutable.';
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Create trigger for UPDATE
DROP TRIGGER IF EXISTS prevent_audit_log_update ON audit_log;
CREATE TRIGGER prevent_audit_log_update
    BEFORE UPDATE ON audit_log
    FOR EACH ROW
    EXECUTE FUNCTION prevent_audit_log_modification();

-- Create trigger for DELETE
DROP TRIGGER IF EXISTS prevent_audit_log_delete ON audit_log;
CREATE TRIGGER prevent_audit_log_delete
    BEFORE DELETE ON audit_log
    FOR EACH ROW
    EXECUTE FUNCTION prevent_audit_log_modification();

-- Comment for documentation
COMMENT ON TABLE audit_log IS 'Immutable audit trail of system and admin actions. INSERT only - UPDATE and DELETE are blocked by trigger.';
