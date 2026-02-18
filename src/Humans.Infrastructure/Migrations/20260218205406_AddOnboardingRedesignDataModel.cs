using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingRedesignDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "ConsentCheckAt",
                table: "profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsentCheckNotes",
                table: "profiles",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsentCheckStatus",
                table: "profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConsentCheckedByUserId",
                table: "profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MembershipTier",
                table: "profiles",
                type: "text",
                nullable: false,
                defaultValue: "Volunteer");

            migrationBuilder.AddColumn<Instant>(
                name: "RejectedAt",
                table: "profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RejectedByUserId",
                table: "profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "profiles",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<LocalDate>(
                name: "BoardMeetingDate",
                table: "applications",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionNote",
                table: "applications",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MembershipTier",
                table: "applications",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<LocalDate>(
                name: "TermExpiresAt",
                table: "applications",
                type: "date",
                nullable: true);

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
                    table.ForeignKey(
                        name: "FK_board_votes_users_BoardMemberUserId",
                        column: x => x.BoardMemberUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "CreatedAt", "Description", "IsActive", "Name", "Slug", "SystemTeamType", "UpdatedAt" },
                values: new object[] { new Guid("00000000-0000-0000-0001-000000000005"), NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), "Active contributors with approved colaborador applications", true, "Colaboradors", "colaboradors", "Colaboradors", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) });

            migrationBuilder.CreateIndex(
                name: "IX_profiles_ConsentCheckStatus",
                table: "profiles",
                column: "ConsentCheckStatus");

            migrationBuilder.CreateIndex(
                name: "IX_applications_MembershipTier",
                table: "applications",
                column: "MembershipTier");

            migrationBuilder.CreateIndex(
                name: "IX_board_votes_ApplicationId",
                table: "board_votes",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_board_votes_ApplicationId_BoardMemberUserId",
                table: "board_votes",
                columns: new[] { "ApplicationId", "BoardMemberUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_board_votes_BoardMemberUserId",
                table: "board_votes",
                column: "BoardMemberUserId");

            // Grandfather existing approved users: set ConsentCheckStatus = 'Cleared', MembershipTier = 'Volunteer'
            // These users bypass the new consent check gate since they were already approved before rollout.
            migrationBuilder.Sql("""
                UPDATE profiles
                SET "ConsentCheckStatus" = 'Cleared',
                    "MembershipTier" = 'Volunteer'
                WHERE "IsApproved" = true;
                """);

            // Set existing approved Asociado applications to MembershipTier = 'Asociado'
            // and compute TermExpiresAt as Dec 31 of the next odd year (synchronized 2-year cycles).
            migrationBuilder.Sql("""
                UPDATE applications
                SET "MembershipTier" = 'Asociado'
                WHERE "Status" = 'Approved';
                """);

            // Compute TermExpiresAt for existing approved applications:
            // If resolved in an odd year, expires Dec 31 of that year + 2 (next odd year).
            // If resolved in an even year, expires Dec 31 of the next odd year.
            migrationBuilder.Sql("""
                UPDATE applications
                SET "TermExpiresAt" = CASE
                    WHEN EXTRACT(YEAR FROM "ResolvedAt") :: int % 2 = 1
                        THEN make_date(EXTRACT(YEAR FROM "ResolvedAt") :: int + 2, 12, 31)
                    ELSE
                        make_date(EXTRACT(YEAR FROM "ResolvedAt") :: int + 1, 12, 31)
                    END
                WHERE "Status" = 'Approved'
                  AND "ResolvedAt" IS NOT NULL;
                """);

            // Set MembershipTier on profiles for users with approved Asociado applications
            migrationBuilder.Sql("""
                UPDATE profiles p
                SET "MembershipTier" = 'Asociado'
                FROM applications a
                WHERE a."UserId" = p."UserId"
                  AND a."Status" = 'Approved'
                  AND a."MembershipTier" = 'Asociado';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "board_votes");

            migrationBuilder.DropIndex(
                name: "IX_profiles_ConsentCheckStatus",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "IX_applications_MembershipTier",
                table: "applications");

            migrationBuilder.DeleteData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"));

            migrationBuilder.DropColumn(
                name: "ConsentCheckAt",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "ConsentCheckNotes",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "ConsentCheckStatus",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "ConsentCheckedByUserId",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "MembershipTier",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "RejectedByUserId",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "BoardMeetingDate",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "DecisionNote",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "MembershipTier",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "TermExpiresAt",
                table: "applications");
        }
    }
}
