using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Teams.Data.Migrations
{
    /// <inheritdoc />
    public partial class BaselineTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Slug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SystemTeamType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GoogleGroupPrefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CustomSlug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsPublicPage = table.Column<bool>(type: "boolean", nullable: false),
                    ShowCoordinatorsOnPublicPage = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    PageContent = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: true),
                    PageContentUpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    PageContentUpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CallsToAction = table.Column<string>(type: "jsonb", nullable: true),
                    HasBudget = table.Column<bool>(type: "boolean", nullable: false),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    IsSensitive = table.Column<bool>(type: "boolean", nullable: false),
                    EarlyEntryEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ParentTeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsPromotedToDirectory = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_teams_teams_ParentTeamId",
                        column: x => x.ParentTeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "team_early_entry_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryDate = table.Column<LocalDate>(type: "date", nullable: false),
                    ProjectName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_early_entry_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_early_entry_grants_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_join_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_join_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_join_requests_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    JoinedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LeftAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_members_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_role_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SlotCount = table.Column<int>(type: "integer", nullable: false),
                    EstimatedHours = table.Column<int>(type: "integer", nullable: true),
                    Priorities = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, defaultValueSql: "''"),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Period = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsManagement = table.Column<bool>(type: "boolean", nullable: false)
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
                name: "team_join_request_state_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamJoinRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_join_request_state_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_join_request_state_history_team_join_requests_TeamJoin~",
                        column: x => x.TeamJoinRequestId,
                        principalTable: "team_join_requests",
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
                });

            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "CallsToAction", "CreatedAt", "CustomSlug", "Description", "EarlyEntryEnabled", "GoogleGroupPrefix", "HasBudget", "IsActive", "IsHidden", "IsPromotedToDirectory", "IsPublicPage", "IsSensitive", "Name", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId", "ParentTeamId", "ShowCoordinatorsOnPublicPage", "Slug", "SystemTeamType", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0001-000000000001"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "All active volunteers with signed required documents", false, null, false, true, false, false, false, false, "Volunteers", null, null, null, null, true, "volunteers", "Volunteers", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000002"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "All team coordinators", false, null, false, true, false, false, false, false, "Coordinators", null, null, null, null, true, "coordinators", "Coordinators", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000003"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "Board members with active role assignments", false, null, false, true, false, false, false, false, "Board", null, null, null, null, true, "board", "Board", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000004"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "Voting members with approved asociado applications", false, null, false, true, false, false, false, false, "Asociados", null, null, null, null, true, "asociados", "Asociados", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000005"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "Active contributors with approved colaborador applications", false, null, false, true, false, false, false, false, "Colaboradors", null, null, null, null, true, "colaboradors", "Colaboradors", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000006"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "All active camp leads across all camps", false, null, false, true, false, false, false, false, "Barrio Leads", null, null, null, null, true, "barrio-leads", "BarrioLeads", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_early_entry_grants_TeamId",
                table: "team_early_entry_grants",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_team_early_entry_grants_UserId",
                table: "team_early_entry_grants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_ChangedAt",
                table: "team_join_request_state_history",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_TeamJoinRequestId",
                table: "team_join_request_state_history",
                column: "TeamJoinRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_Status",
                table: "team_join_requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_TeamId",
                table: "team_join_requests",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_TeamId_UserId_Status",
                table: "team_join_requests",
                columns: new[] { "TeamId", "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_UserId",
                table: "team_join_requests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_active_unique",
                table: "team_members",
                columns: new[] { "TeamId", "UserId" },
                unique: true,
                filter: "\"LeftAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_Role",
                table: "team_members",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_UserId",
                table: "team_members",
                column: "UserId");

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

            migrationBuilder.CreateIndex(
                name: "IX_team_role_definitions_team_name_unique",
                table: "team_role_definitions",
                columns: new[] { "TeamId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_role_definitions_TeamId",
                table: "team_role_definitions",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_teams_CustomSlug",
                table: "teams",
                column: "CustomSlug",
                unique: true,
                filter: "\"CustomSlug\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_teams_GoogleGroupPrefix",
                table: "teams",
                column: "GoogleGroupPrefix",
                unique: true,
                filter: "\"GoogleGroupPrefix\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_teams_IsActive",
                table: "teams",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_teams_ParentTeamId",
                table: "teams",
                column: "ParentTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_teams_Slug",
                table: "teams",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teams_SystemTeamType",
                table: "teams",
                column: "SystemTeamType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_early_entry_grants");

            migrationBuilder.DropTable(
                name: "team_join_request_state_history");

            migrationBuilder.DropTable(
                name: "team_role_assignments");

            migrationBuilder.DropTable(
                name: "team_join_requests");

            migrationBuilder.DropTable(
                name: "team_members");

            migrationBuilder.DropTable(
                name: "team_role_definitions");

            migrationBuilder.DropTable(
                name: "teams");
        }
    }
}
