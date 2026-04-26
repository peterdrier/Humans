using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContainerPlacementPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "ContainerPlacementClosedAt",
                table: "city_planning_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "ContainerPlacementOpenedAt",
                table: "city_planning_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsContainerPlacementOpen",
                table: "city_planning_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContainerPlacementClosedAt",
                table: "city_planning_settings");

            migrationBuilder.DropColumn(
                name: "ContainerPlacementOpenedAt",
                table: "city_planning_settings");

            migrationBuilder.DropColumn(
                name: "IsContainerPlacementOpen",
                table: "city_planning_settings");
        }
    }
}
