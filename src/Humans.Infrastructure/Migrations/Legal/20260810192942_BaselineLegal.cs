using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations.Legal
{
    /// <summary>
    /// Baseline for the Legal section peel (nobodies-collective/Humans#858):
    /// <c>legal_documents</c>, <c>document_versions</c> and
    /// <c>consent_records</c> move to <c>LegalDbContext</c>, which owns the
    /// physical tables from here on. The <c>migrationBuilder.Sql</c> block at
    /// the end of <c>Up()</c> reproduces the <c>consent_records</c> plpgsql
    /// immutability trigger verbatim from the old chain's Initial migration —
    /// Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> (2026-08-10
    /// decision on nobodies-collective/Humans#858): the trigger exists only as
    /// raw SQL, never in the EF model, so a model-generated baseline would
    /// silently omit it and fresh databases would diverge from every
    /// chain-built database.
    /// </summary>
    public partial class BaselineLegal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legal_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    GracePeriodDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 7),
                    GitHubFolderPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CurrentCommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "document_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LegalDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Content = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    EffectiveFrom = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    RequiresReConsent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ChangesSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_versions_legal_documents_LegalDocumentId",
                        column: x => x.LegalDocumentId,
                        principalTable: "legal_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsentedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExplicitConsent = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consent_records_document_versions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "document_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_ConsentedAt",
                table: "consent_records",
                column: "ConsentedAt");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_DocumentVersionId",
                table: "consent_records",
                column: "DocumentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_UserId",
                table: "consent_records",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_UserId_DocumentVersionId",
                table: "consent_records",
                columns: new[] { "UserId", "DocumentVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_UserId_ExplicitConsent_ConsentedAt",
                table: "consent_records",
                columns: new[] { "UserId", "ExplicitConsent", "ConsentedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_CommitSha",
                table: "document_versions",
                column: "CommitSha");

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_EffectiveFrom",
                table: "document_versions",
                column: "EffectiveFrom");

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_LegalDocumentId",
                table: "document_versions",
                column: "LegalDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_legal_documents_IsActive",
                table: "legal_documents",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_legal_documents_TeamId_IsActive",
                table: "legal_documents",
                columns: new[] { "TeamId", "IsActive" });

            // Immutability trigger for GDPR audit trail compliance — verbatim
            // from the old chain's Initial migration (see class doc comment).
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS prevent_consent_record_update ON consent_records;
                DROP TRIGGER IF EXISTS prevent_consent_record_delete ON consent_records;
                DROP FUNCTION IF EXISTS prevent_consent_record_modification();
                """);

            migrationBuilder.DropTable(
                name: "consent_records");

            migrationBuilder.DropTable(
                name: "document_versions");

            migrationBuilder.DropTable(
                name: "legal_documents");
        }
    }
}
