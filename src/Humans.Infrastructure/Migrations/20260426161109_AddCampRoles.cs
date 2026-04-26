using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "camp_role_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SlotCount = table.Column<int>(type: "integer", nullable: false),
                    MinimumRequired = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_role_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "camp_role_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampRoleDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_role_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_role_assignments_camp_members_CampMemberId",
                        column: x => x.CampMemberId,
                        principalTable: "camp_members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_camp_role_assignments_camp_role_definitions_CampRoleDefinit~",
                        column: x => x.CampRoleDefinitionId,
                        principalTable: "camp_role_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camp_role_assignments_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_assignments_CampMemberId",
                table: "camp_role_assignments",
                column: "CampMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_assignments_CampRoleDefinitionId",
                table: "camp_role_assignments",
                column: "CampRoleDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_assignments_unique",
                table: "camp_role_assignments",
                columns: new[] { "CampSeasonId", "CampRoleDefinitionId", "CampMemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_definitions_name_unique",
                table: "camp_role_definitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_definitions_SortOrder",
                table: "camp_role_definitions",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "camp_role_assignments");

            migrationBuilder.DropTable(
                name: "camp_role_definitions");
        }
    }
}
