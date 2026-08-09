using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations.Governance
{
    /// <inheritdoc />
    public partial class BaselineGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipTier = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Motivation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AdditionalInfo = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SignificantContribution = table.Column<string>(type: "text", nullable: true),
                    RoleUnderstanding = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SubmittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ReviewStartedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TermExpiresAt = table.Column<LocalDate>(type: "date", nullable: true),
                    BoardMeetingDate = table.Column<LocalDate>(type: "date", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RenewalReminderSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "application_state_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_state_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_application_state_history_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "board_votes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardMemberUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Vote = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    VotedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_board_votes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_board_votes_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_state_history_ApplicationId",
                table: "application_state_history",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_application_state_history_ChangedAt",
                table: "application_state_history",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_applications_MembershipTier",
                table: "applications",
                column: "MembershipTier");

            migrationBuilder.CreateIndex(
                name: "IX_applications_Status",
                table: "applications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_applications_SubmittedAt",
                table: "applications",
                column: "SubmittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_applications_UserId",
                table: "applications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_UserId_Status",
                table: "applications",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_board_votes_ApplicationId",
                table: "board_votes",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_board_votes_ApplicationId_BoardMemberUserId",
                table: "board_votes",
                columns: new[] { "ApplicationId", "BoardMemberUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_state_history");

            migrationBuilder.DropTable(
                name: "board_votes");

            migrationBuilder.DropTable(
                name: "applications");
        }
    }
}
