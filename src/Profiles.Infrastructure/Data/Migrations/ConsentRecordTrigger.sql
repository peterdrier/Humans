-- PostgreSQL trigger to prevent UPDATE and DELETE on consent_records table
-- This ensures the append-only nature of the consent audit trail

-- Function that raises an exception on UPDATE or DELETE
CREATE OR REPLACE FUNCTION prevent_consent_record_modification()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'UPDATE operations are not allowed on consent_records table. Consent records are immutable for audit trail purposes.';
    ELSIF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'DELETE operations are not allowed on consent_records table. Consent records are immutable for audit trail purposes.';
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Create trigger for UPDATE
DROP TRIGGER IF EXISTS prevent_consent_record_update ON consent_records;
CREATE TRIGGER prevent_consent_record_update
    BEFORE UPDATE ON consent_records
    FOR EACH ROW
    EXECUTE FUNCTION prevent_consent_record_modification();

-- Create trigger for DELETE
DROP TRIGGER IF EXISTS prevent_consent_record_delete ON consent_records;
CREATE TRIGGER prevent_consent_record_delete
    BEFORE DELETE ON consent_records
    FOR EACH ROW
    EXECUTE FUNCTION prevent_consent_record_modification();

-- Comment for documentation
COMMENT ON TABLE consent_records IS 'Immutable audit trail of user consent. INSERT only - UPDATE and DELETE are blocked by trigger.';
