using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "camp_map_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    IsPlacementOpen = table.Column<bool>(type: "boolean", nullable: false),
                    OpenedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LimitZoneGeoJson = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_map_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "camp_polygon_histories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeoJson = table.Column<string>(type: "text", nullable: false),
                    AreaSqm = table.Column<double>(type: "double precision", nullable: false),
                    ModifiedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_polygon_histories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_polygon_histories_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camp_polygon_histories_users_ModifiedByUserId",
                        column: x => x.ModifiedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "camp_polygons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeoJson = table.Column<string>(type: "text", nullable: false),
                    AreaSqm = table.Column<double>(type: "double precision", nullable: false),
                    LastModifiedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastModifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_polygons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_polygons_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camp_polygons_users_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_camp_map_settings_Year",
                table: "camp_map_settings",
                column: "Year",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygon_histories_CampSeasonId_ModifiedAt",
                table: "camp_polygon_histories",
                columns: new[] { "CampSeasonId", "ModifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygon_histories_ModifiedByUserId",
                table: "camp_polygon_histories",
                column: "ModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygons_CampSeasonId",
                table: "camp_polygons",
                column: "CampSeasonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygons_LastModifiedByUserId",
                table: "camp_polygons",
                column: "LastModifiedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "camp_map_settings");

            migrationBuilder.DropTable(
                name: "camp_polygon_histories");

            migrationBuilder.DropTable(
                name: "camp_polygons");
        }
    }
}
