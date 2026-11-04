using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropUsersLegacyStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "communication_preferences");

            migrationBuilder.DropTable(
                name: "event_participations");

            migrationBuilder.DropIndex(
                name: "IX_profiles_ConsentCheckStatus",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "GoogleEmailStatus",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastConsentReminderSentAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Allergies",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "AllergyOtherText",
                table: "profiles");

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
                name: "DietaryPreference",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "Iban",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "IntoleranceOtherText",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "Intolerances",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "IsApproved",
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoogleEmailStatus",
                table: "users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<Instant>(
                name: "LastConsentReminderSentAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Allergies",
                table: "profiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AllergyOtherText",
                table: "profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

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
                name: "DietaryPreference",
                table: "profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Iban",
                table: "profiles",
                type: "character varying(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntoleranceOtherText",
                table: "profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Intolerances",
                table: "profiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsApproved",
                table: "profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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

            migrationBuilder.CreateTable(
                name: "communication_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InboxEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    OptedOut = table.Column<bool>(type: "boolean", nullable: false),
                    SubscribedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    UpdateSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_communication_preferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_communication_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_participations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedInAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DeclaredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_participations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_event_participations_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_profiles_ConsentCheckStatus",
                table: "profiles",
                column: "ConsentCheckStatus");

            migrationBuilder.CreateIndex(
                name: "IX_communication_preferences_UserId",
                table: "communication_preferences",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_communication_preferences_UserId_Category",
                table: "communication_preferences",
                columns: new[] { "UserId", "Category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_participations_UserId_Year",
                table: "event_participations",
                columns: new[] { "UserId", "Year" },
                unique: true);
        }
    }
}
