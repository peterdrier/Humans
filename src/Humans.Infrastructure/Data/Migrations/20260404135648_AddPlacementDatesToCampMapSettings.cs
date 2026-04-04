using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlacementDatesToCampMapSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LocalDateTime>(
                name: "PlacementClosesAt",
                table: "camp_map_settings",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<LocalDateTime>(
                name: "PlacementOpensAt",
                table: "camp_map_settings",
                type: "timestamp without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlacementClosesAt",
                table: "camp_map_settings");

            migrationBuilder.DropColumn(
                name: "PlacementOpensAt",
                table: "camp_map_settings");
        }
    }
}
