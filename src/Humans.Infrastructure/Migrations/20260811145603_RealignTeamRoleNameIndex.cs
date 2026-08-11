using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Replaces the hand-written functional unique index
    /// <c>IX_team_role_definitions_team_name_unique (TeamId, lower(Name))</c>
    /// (raw SQL in <c>20260311154351_AddTeamRoleSlots</c>) with the plain
    /// <c>(TeamId, Name)</c> unique index the model has declared all along —
    /// Peter's call on the peel-14 wall (2026-08-11): no custom SQL in
    /// migrations, realign physical to model. The functional index is strictly
    /// stronger, so existing data cannot violate the plain one. Hand-authored
    /// operations are a Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> — EF cannot
    /// scaffold this because model and snapshot already agree. The same-PR
    /// <c>DropIndex</c> is authorized per the index-swap exception pattern in
    /// <c>memory/architecture/no-drops-until-prod-verified.md</c>: same name,
    /// index-only, no data touched, rebuildable from its definition.
    /// Unblocks the Teams peel (nobodies-collective/Humans#858) — the
    /// schema-equivalence gate compares <c>indexdef</c>, so chain-built and
    /// baseline-built schema must agree on this index.
    /// </summary>
    public partial class RealignTeamRoleNameIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_team_role_definitions_team_name_unique",
                table: "team_role_definitions");

            migrationBuilder.CreateIndex(
                name: "IX_team_role_definitions_team_name_unique",
                table: "team_role_definitions",
                columns: new[] { "TeamId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Deliberately empty: the model has declared the plain index since
        /// <c>AddTeamRoleSlots</c> (every historical Designer records it), so the
        /// plain index IS the model-faithful state below this migration too.
        /// Restoring the functional variant would need the raw SQL this
        /// migration exists to excise.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
