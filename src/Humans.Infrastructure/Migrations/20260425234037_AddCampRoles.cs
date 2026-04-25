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

            var consentLeadId = Guid.Parse("11111111-aaaa-4000-8000-000000000001");
            var lntId         = Guid.Parse("11111111-aaaa-4000-8000-000000000002");
            var shitNinjaId   = Guid.Parse("11111111-aaaa-4000-8000-000000000003");
            var powerId       = Guid.Parse("11111111-aaaa-4000-8000-000000000004");
            var buildLeadId   = Guid.Parse("11111111-aaaa-4000-8000-000000000005");

            var seedAt = NodaTime.Instant.FromUnixTimeTicks(17463600000000000L); // 2026-04-26 00:00:00 UTC, deterministic

            migrationBuilder.InsertData(
                table: "camp_role_definitions",
                columns: new[] { "Id", "Name", "Description", "SlotCount", "MinimumRequired", "SortOrder", "IsRequired", "CreatedAt", "UpdatedAt", "DeactivatedAt" },
                values: new object[,]
                {
                    { consentLeadId, "Consent Lead", null, 2, 1, 10, true,  seedAt, seedAt, null },
                    { lntId,         "LNT",          null, 1, 1, 20, true,  seedAt, seedAt, null },
                    { shitNinjaId,   "Shit Ninja",   null, 1, 1, 30, true,  seedAt, seedAt, null },
                    { powerId,       "Power",        null, 1, 0, 40, false, seedAt, seedAt, null },
                    { buildLeadId,   "Build Lead",   null, 2, 1, 50, true,  seedAt, seedAt, null },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "camp_role_definitions",
                keyColumn: "Id",
                keyValues: new object[]
                {
                    Guid.Parse("11111111-aaaa-4000-8000-000000000001"),
                    Guid.Parse("11111111-aaaa-4000-8000-000000000002"),
                    Guid.Parse("11111111-aaaa-4000-8000-000000000003"),
                    Guid.Parse("11111111-aaaa-4000-8000-000000000004"),
                    Guid.Parse("11111111-aaaa-4000-8000-000000000005"),
                });

            migrationBuilder.DropTable(
                name: "camp_role_assignments");

            migrationBuilder.DropTable(
                name: "camp_role_definitions");
        }
    }
}
