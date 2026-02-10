using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Adds database triggers to enforce immutability on consent_records and audit_log tables.
    /// These tables are append-only for GDPR audit trail compliance.
    /// </summary>
    public partial class AddImmutabilityTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Consent records immutability trigger
            migrationBuilder.Sql("""
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

                DROP TRIGGER IF EXISTS prevent_consent_record_update ON consent_records;
                CREATE TRIGGER prevent_consent_record_update
                    BEFORE UPDATE ON consent_records
                    FOR EACH ROW
                    EXECUTE FUNCTION prevent_consent_record_modification();

                DROP TRIGGER IF EXISTS prevent_consent_record_delete ON consent_records;
                CREATE TRIGGER prevent_consent_record_delete
                    BEFORE DELETE ON consent_records
                    FOR EACH ROW
                    EXECUTE FUNCTION prevent_consent_record_modification();

                COMMENT ON TABLE consent_records IS 'Immutable audit trail of user consent. INSERT only - UPDATE and DELETE are blocked by trigger.';
                """);

            // Audit log immutability trigger
            migrationBuilder.Sql("""
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

                DROP TRIGGER IF EXISTS prevent_audit_log_update ON audit_log;
                CREATE TRIGGER prevent_audit_log_update
                    BEFORE UPDATE ON audit_log
                    FOR EACH ROW
                    EXECUTE FUNCTION prevent_audit_log_modification();

                DROP TRIGGER IF EXISTS prevent_audit_log_delete ON audit_log;
                CREATE TRIGGER prevent_audit_log_delete
                    BEFORE DELETE ON audit_log
                    FOR EACH ROW
                    EXECUTE FUNCTION prevent_audit_log_modification();

                COMMENT ON TABLE audit_log IS 'Immutable audit trail of system and admin actions. INSERT only - UPDATE and DELETE are blocked by trigger.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS prevent_consent_record_update ON consent_records;
                DROP TRIGGER IF EXISTS prevent_consent_record_delete ON consent_records;
                DROP FUNCTION IF EXISTS prevent_consent_record_modification();
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS prevent_audit_log_update ON audit_log;
                DROP TRIGGER IF EXISTS prevent_audit_log_delete ON audit_log;
                DROP FUNCTION IF EXISTS prevent_audit_log_modification();
                """);
        }
    }
}
