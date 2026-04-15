using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "camp_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RequestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RemovedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RemovedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_members_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_camp_members_users_ConfirmedByUserId",
                        column: x => x.ConfirmedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_camp_members_users_RemovedByUserId",
                        column: x => x.RemovedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_camp_members_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_camp_members_active_unique",
                table: "camp_members",
                columns: new[] { "CampSeasonId", "UserId" },
                unique: true,
                filter: "\"Status\" <> 'Removed'");

            migrationBuilder.CreateIndex(
                name: "IX_camp_members_ConfirmedByUserId",
                table: "camp_members",
                column: "ConfirmedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_members_RemovedByUserId",
                table: "camp_members",
                column: "RemovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_members_UserId",
                table: "camp_members",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "camp_members");
        }
    }
}
