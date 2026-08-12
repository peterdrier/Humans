using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Teams peel
    /// (nobodies-collective/Humans#858): <c>teams</c>, <c>team_members</c>,
    /// <c>team_join_requests</c>, <c>team_join_request_state_history</c>,
    /// <c>team_role_definitions</c>, <c>team_role_assignments</c> and
    /// <c>team_early_entry_grants</c> moved to <c>TeamsDbContext</c>, which
    /// owns the physical tables from here on. The scaffolded
    /// <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately emptied —
    /// Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelTeams : Migration
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
