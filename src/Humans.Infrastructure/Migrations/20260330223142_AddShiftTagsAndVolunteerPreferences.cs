using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftTagsAndVolunteerPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shift_tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rota_shift_tags",
                columns: table => new
                {
                    RotaId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftTagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rota_shift_tags", x => new { x.RotaId, x.ShiftTagId });
                    table.ForeignKey(
                        name: "FK_rota_shift_tags_rotas_RotaId",
                        column: x => x.RotaId,
                        principalTable: "rotas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rota_shift_tags_shift_tags_ShiftTagId",
                        column: x => x.ShiftTagId,
                        principalTable: "shift_tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volunteer_tag_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftTagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_tag_preferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volunteer_tag_preferences_shift_tags_ShiftTagId",
                        column: x => x.ShiftTagId,
                        principalTable: "shift_tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_volunteer_tag_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "shift_tags",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0002-000000000001"), "Heavy lifting" },
                    { new Guid("00000000-0000-0000-0002-000000000002"), "Working in the sun" },
                    { new Guid("00000000-0000-0000-0002-000000000003"), "Working in the shade" },
                    { new Guid("00000000-0000-0000-0002-000000000004"), "Organisational task" },
                    { new Guid("00000000-0000-0000-0002-000000000005"), "Meeting new people" },
                    { new Guid("00000000-0000-0000-0002-000000000006"), "Looking after folks" },
                    { new Guid("00000000-0000-0000-0002-000000000007"), "Exploring the site" },
                    { new Guid("00000000-0000-0000-0002-000000000008"), "Feeding and hydrating folks" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_rota_shift_tags_ShiftTagId",
                table: "rota_shift_tags",
                column: "ShiftTagId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_tags_name_unique",
                table: "shift_tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_tag_preferences_ShiftTagId",
                table: "volunteer_tag_preferences",
                column: "ShiftTagId");

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_tag_preferences_user_tag_unique",
                table: "volunteer_tag_preferences",
                columns: new[] { "UserId", "ShiftTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_tag_preferences_UserId",
                table: "volunteer_tag_preferences",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rota_shift_tags");

            migrationBuilder.DropTable(
                name: "volunteer_tag_preferences");

            migrationBuilder.DropTable(
                name: "shift_tags");
        }
    }
}
