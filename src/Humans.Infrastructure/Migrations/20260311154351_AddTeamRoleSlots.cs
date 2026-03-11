using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamRoleSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_role_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SlotCount = table.Column<int>(type: "integer", nullable: false),
                    Priorities = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, defaultValueSql: "''"),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_role_definitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_role_definitions_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_role_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamRoleDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotIndex = table.Column<int>(type: "integer", nullable: false),
                    AssignedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_role_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_role_assignments_team_members_TeamMemberId",
                        column: x => x.TeamMemberId,
                        principalTable: "team_members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_team_role_assignments_team_role_definitions_TeamRoleDefinit~",
                        column: x => x.TeamRoleDefinitionId,
                        principalTable: "team_role_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_role_assignments_users_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_role_assignments_AssignedByUserId",
                table: "team_role_assignments",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_role_assignments_definition_member_unique",
                table: "team_role_assignments",
                columns: new[] { "TeamRoleDefinitionId", "TeamMemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_role_assignments_definition_slot_unique",
                table: "team_role_assignments",
                columns: new[] { "TeamRoleDefinitionId", "SlotIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_role_assignments_TeamMemberId",
                table: "team_role_assignments",
                column: "TeamMemberId");

            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX ""IX_team_role_definitions_team_name_unique""
                  ON team_role_definitions (""TeamId"", lower(""Name""))");

            migrationBuilder.CreateIndex(
                name: "IX_team_role_definitions_TeamId",
                table: "team_role_definitions",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_role_assignments");

            migrationBuilder.DropTable(
                name: "team_role_definitions");
        }
    }
}
