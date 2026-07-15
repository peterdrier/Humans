using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the SystemSettings peel
    /// (nobodies-collective/Humans#858): <c>system_settings</c> moved to
    /// <c>SystemSettingsDbContext</c>, which owns the physical table from here on.
    /// The scaffolded <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately
    /// emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelSystemSettings : Migration
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
