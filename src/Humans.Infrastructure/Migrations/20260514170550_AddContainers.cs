using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContainers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContainerCount",
                table: "camp_seasons");

            migrationBuilder.DropColumn(
                name: "ContainerNotes",
                table: "camp_seasons");

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

            migrationBuilder.CreateTable(
                name: "container_placements",
                columns: table => new
                {
                    ContainerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    LocationGeoJson = table.Column<string>(type: "text", nullable: true),
                    PlacementNotes = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    PlacementImageStoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PlacementImageContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlacementImageFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_placements", x => new { x.ContainerId, x.Year });
                });

            migrationBuilder.CreateTable(
                name: "containers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ImageStoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ImageContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImageFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_containers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_container_placements_Year",
                table: "container_placements",
                column: "Year");

            migrationBuilder.CreateIndex(
                name: "IX_containers_CampId",
                table: "containers",
                column: "CampId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "container_placements");

            migrationBuilder.DropTable(
                name: "containers");

            migrationBuilder.DropColumn(
                name: "ContainerPlacementClosedAt",
                table: "city_planning_settings");

            migrationBuilder.DropColumn(
                name: "ContainerPlacementOpenedAt",
                table: "city_planning_settings");

            migrationBuilder.DropColumn(
                name: "IsContainerPlacementOpen",
                table: "city_planning_settings");

            migrationBuilder.AddColumn<int>(
                name: "ContainerCount",
                table: "camp_seasons",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ContainerNotes",
                table: "camp_seasons",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }
    }
}
