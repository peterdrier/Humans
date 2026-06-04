using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBarrioShiftObligations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shift_obligations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampRoleSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Applicability = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DefaultRequiredShiftCount = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_obligations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "camp_season_shift_obligations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftObligationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequiredShiftCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_season_shift_obligations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_season_shift_obligations_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_camp_season_shift_obligations_shift_obligations_ShiftObliga~",
                        column: x => x.ShiftObligationId,
                        principalTable: "shift_obligations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_camp_season_shift_obligations_CampSeasonId",
                table: "camp_season_shift_obligations",
                column: "CampSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_season_shift_obligations_ShiftObligationId",
                table: "camp_season_shift_obligations",
                column: "ShiftObligationId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_season_shift_obligations_unique",
                table: "camp_season_shift_obligations",
                columns: new[] { "CampSeasonId", "ShiftObligationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shift_obligations_target_unique",
                table: "shift_obligations",
                columns: new[] { "TargetType", "TargetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "camp_season_shift_obligations");

            migrationBuilder.DropTable(
                name: "shift_obligations");
        }
    }
}
