using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.CityPlanning.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_city_planning_settings",
                table: "city_planning_settings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_camp_polygons",
                table: "camp_polygons");

            migrationBuilder.DropPrimaryKey(
                name: "PK_camp_polygon_histories",
                table: "camp_polygon_histories");

            migrationBuilder.RenameTable(
                name: "city_planning_settings",
                newName: "city_settings");

            migrationBuilder.RenameTable(
                name: "camp_polygons",
                newName: "city_camp_polygons");

            migrationBuilder.RenameTable(
                name: "camp_polygon_histories",
                newName: "city_camp_polygon_history");

            migrationBuilder.RenameIndex(
                name: "IX_city_planning_settings_Year",
                table: "city_settings",
                newName: "IX_city_settings_Year");

            migrationBuilder.RenameIndex(
                name: "IX_camp_polygons_CampSeasonId",
                table: "city_camp_polygons",
                newName: "IX_city_camp_polygons_CampSeasonId");

            migrationBuilder.RenameIndex(
                name: "IX_camp_polygon_histories_CampSeasonId_ModifiedAt",
                table: "city_camp_polygon_history",
                newName: "IX_city_camp_polygon_history_CampSeasonId_ModifiedAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_city_settings",
                table: "city_settings",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_city_camp_polygons",
                table: "city_camp_polygons",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_city_camp_polygon_history",
                table: "city_camp_polygon_history",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_city_settings",
                table: "city_settings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_city_camp_polygons",
                table: "city_camp_polygons");

            migrationBuilder.DropPrimaryKey(
                name: "PK_city_camp_polygon_history",
                table: "city_camp_polygon_history");

            migrationBuilder.RenameTable(
                name: "city_settings",
                newName: "city_planning_settings");

            migrationBuilder.RenameTable(
                name: "city_camp_polygons",
                newName: "camp_polygons");

            migrationBuilder.RenameTable(
                name: "city_camp_polygon_history",
                newName: "camp_polygon_histories");

            migrationBuilder.RenameIndex(
                name: "IX_city_settings_Year",
                table: "city_planning_settings",
                newName: "IX_city_planning_settings_Year");

            migrationBuilder.RenameIndex(
                name: "IX_city_camp_polygons_CampSeasonId",
                table: "camp_polygons",
                newName: "IX_camp_polygons_CampSeasonId");

            migrationBuilder.RenameIndex(
                name: "IX_city_camp_polygon_history_CampSeasonId_ModifiedAt",
                table: "camp_polygon_histories",
                newName: "IX_camp_polygon_histories_CampSeasonId_ModifiedAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_city_planning_settings",
                table: "city_planning_settings",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_camp_polygons",
                table: "camp_polygons",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_camp_polygon_histories",
                table: "camp_polygon_histories",
                column: "Id");
        }
    }
}
