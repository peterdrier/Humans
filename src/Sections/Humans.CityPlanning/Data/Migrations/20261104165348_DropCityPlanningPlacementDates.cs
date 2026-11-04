using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.CityPlanning.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropCityPlanningPlacementDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlacementClosesAt",
                table: "city_planning_settings");

            migrationBuilder.DropColumn(
                name: "PlacementOpensAt",
                table: "city_planning_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LocalDateTime>(
                name: "PlacementClosesAt",
                table: "city_planning_settings",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<LocalDateTime>(
                name: "PlacementOpensAt",
                table: "city_planning_settings",
                type: "timestamp without time zone",
                nullable: true);
        }
    }
}
