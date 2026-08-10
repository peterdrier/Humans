using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Gate and System peels
    /// (nobodies-collective/Humans#858): <c>gate_scan_events</c>,
    /// <c>gate_settings</c> and <c>gate_staff_pins</c> moved to
    /// <c>GateDbContext</c>, and <c>DataProtectionKeys</c> moved to
    /// <c>SystemDbContext</c>, which own the physical tables from here on.
    /// The scaffolded <c>DropTable</c>/<c>CreateTable</c> bodies were
    /// deliberately emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelGateAndDataProtectionKeys : Migration
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
