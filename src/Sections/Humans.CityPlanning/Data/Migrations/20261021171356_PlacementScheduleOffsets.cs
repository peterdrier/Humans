using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.CityPlanning.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlacementScheduleOffsets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsPlacementOpen",
                table: "city_planning_settings",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AddColumn<int>(
                name: "PlacementChangeableUntilOffset",
                table: "city_planning_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlacementClosesOffset",
                table: "city_planning_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlacementOpensOffset",
                table: "city_planning_settings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlacementChangeableUntilOffset",
                table: "city_planning_settings");

            migrationBuilder.DropColumn(
                name: "PlacementClosesOffset",
                table: "city_planning_settings");

            migrationBuilder.DropColumn(
                name: "PlacementOpensOffset",
                table: "city_planning_settings");

            migrationBuilder.AlterColumn<bool>(
                name: "IsPlacementOpen",
                table: "city_planning_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }
    }
}
