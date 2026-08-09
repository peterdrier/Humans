using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the CityPlanning, Budget and Camps peels
    /// (nobodies-collective/Humans#858): <c>city_planning_settings</c>,
    /// <c>camp_polygons</c>, <c>camp_polygon_histories</c>,
    /// <c>budget_years</c>, <c>budget_groups</c>, <c>budget_categories</c>,
    /// <c>budget_line_items</c>, <c>budget_audit_logs</c>,
    /// <c>ticketing_projections</c>, <c>camps</c>, <c>camp_seasons</c>,
    /// <c>camp_historical_names</c>, <c>camp_images</c>, <c>camp_settings</c>,
    /// <c>camp_members</c>, <c>camp_role_definitions</c> and
    /// <c>camp_role_assignments</c> moved to <c>CityPlanningDbContext</c>,
    /// <c>BudgetDbContext</c> and <c>CampsDbContext</c>, which own the physical
    /// tables from here on. The scaffolded
    /// <c>DropTable</c>/<c>CreateTable</c>/<c>InsertData</c> bodies were
    /// deliberately emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema
    /// and re-seeds nothing.
    /// </summary>
    public partial class PeelCityPlanningBudgetCamps : Migration
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
