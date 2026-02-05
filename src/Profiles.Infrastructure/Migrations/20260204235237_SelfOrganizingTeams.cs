using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Profiles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SelfOrganizingTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresApproval",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemTeamType",
                table: "teams",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

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
                    table.ForeignKey(
                        name: "FK_team_join_requests_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_team_join_requests_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
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
                    table.ForeignKey(
                        name: "FK_team_join_request_state_history_users_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_teams_SystemTeamType",
                table: "teams",
                column: "SystemTeamType");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_Role",
                table: "team_members",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_ChangedAt",
                table: "team_join_request_state_history",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_ChangedByUserId",
                table: "team_join_request_state_history",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_TeamJoinRequestId",
                table: "team_join_request_state_history",
                column: "TeamJoinRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_ReviewedByUserId",
                table: "team_join_requests",
                column: "ReviewedByUserId");

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

            // Seed system teams
            var now = NodaTime.SystemClock.Instance.GetCurrentInstant();
            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "Name", "Description", "Slug", "IsActive", "RequiresApproval", "SystemTeamType", "CreatedAt", "UpdatedAt" },
                values: new object[] { Guid.Parse("00000000-0000-0000-0001-000000000001"), "Volunteers", "All active volunteers with signed required documents", "volunteers", true, false, "Volunteers", now, now });

            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "Name", "Description", "Slug", "IsActive", "RequiresApproval", "SystemTeamType", "CreatedAt", "UpdatedAt" },
                values: new object[] { Guid.Parse("00000000-0000-0000-0001-000000000002"), "Metaleads", "All team metaleads", "metaleads", true, false, "Metaleads", now, now });

            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "Name", "Description", "Slug", "IsActive", "RequiresApproval", "SystemTeamType", "CreatedAt", "UpdatedAt" },
                values: new object[] { Guid.Parse("00000000-0000-0000-0001-000000000003"), "Board", "Board members with active role assignments", "board", true, false, "Board", now, now });

            // Update existing team_members.Role from "Member"/"Lead" strings to new enum values
            migrationBuilder.Sql(@"
                UPDATE team_members
                SET ""Role"" = CASE
                    WHEN ""Role"" = 'Lead' THEN 'Metalead'
                    ELSE 'Member'
                END;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_join_request_state_history");

            migrationBuilder.DropTable(
                name: "team_join_requests");

            migrationBuilder.DropIndex(
                name: "IX_teams_SystemTeamType",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "IX_team_members_Role",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "RequiresApproval",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "SystemTeamType",
                table: "teams");
        }
    }
}
