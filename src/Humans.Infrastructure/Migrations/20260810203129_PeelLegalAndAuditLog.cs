using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Legal and AuditLog peels
    /// (nobodies-collective/Humans#858): <c>legal_documents</c>,
    /// <c>document_versions</c> and <c>consent_records</c> moved to
    /// <c>LegalDbContext</c>, and <c>audit_log</c> moved to
    /// <c>AuditLogDbContext</c>, which own the physical tables from here on.
    /// The scaffolded <c>DropTable</c>/<c>CreateTable</c> bodies were
    /// deliberately emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelLegalAndAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
