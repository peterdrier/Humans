using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations.AuditLog
{
    /// <summary>
    /// Baseline for the AuditLog section peel (nobodies-collective/Humans#858):
    /// <c>audit_log</c> moves to <c>AuditLogDbContext</c>, which owns the
    /// physical table from here on. The <c>migrationBuilder.Sql</c> block at
    /// the end of <c>Up()</c> reproduces the <c>audit_log</c> plpgsql
    /// immutability trigger verbatim from the old chain's Initial migration —
    /// Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> (2026-08-10
    /// decision on nobodies-collective/Humans#858): the trigger exists only as
    /// raw SQL, never in the EF model, so a model-generated baseline would
    /// silently omit it and fresh databases would diverge from every
    /// chain-built database.
    /// </summary>
    public partial class BaselineAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedEntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SyncSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserEmail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_Action",
                table: "audit_log",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_EntityType_EntityId",
                table: "audit_log",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_OccurredAt",
                table: "audit_log",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_RelatedEntityType_RelatedEntityId",
                table: "audit_log",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_ResourceId",
                table: "audit_log",
                column: "ResourceId");

            // Immutability trigger — verbatim from the old chain's Initial
            // migration (see class doc comment).
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
                DROP TRIGGER IF EXISTS prevent_audit_log_update ON audit_log;
                DROP TRIGGER IF EXISTS prevent_audit_log_delete ON audit_log;
                DROP FUNCTION IF EXISTS prevent_audit_log_modification();
                """);

            migrationBuilder.DropTable(
                name: "audit_log");
        }
    }
}
