using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Profiles.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
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
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RelatedEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedEntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_log_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_Action",
                table: "audit_log",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_ActorUserId",
                table: "audit_log",
                column: "ActorUserId");

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

            // Immutability trigger — prevent UPDATE and DELETE on audit_log
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
