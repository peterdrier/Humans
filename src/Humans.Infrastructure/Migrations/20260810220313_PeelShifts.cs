using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Shifts peel
    /// (nobodies-collective/Humans#858): <c>event_settings</c>, <c>rotas</c>,
    /// <c>shifts</c>, <c>shift_signups</c>, <c>shift_tags</c>,
    /// <c>rota_shift_tags</c>, <c>volunteer_event_profiles</c>,
    /// <c>general_availability</c>, <c>volunteer_build_statuses</c> and
    /// <c>volunteer_tag_preferences</c> moved to <c>ShiftsDbContext</c>, which
    /// owns the physical tables from here on. The scaffolded
    /// <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately emptied —
    /// Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelShifts : Migration
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
