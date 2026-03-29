using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ICalToken",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShiftSignupId",
                table: "email_outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GateOpeningDate = table.Column<LocalDate>(type: "date", nullable: false),
                    BuildStartOffset = table.Column<int>(type: "integer", nullable: false),
                    EventEndOffset = table.Column<int>(type: "integer", nullable: false),
                    StrikeEndOffset = table.Column<int>(type: "integer", nullable: false),
                    EarlyEntryCapacity = table.Column<string>(type: "jsonb", nullable: false),
                    BarriosEarlyEntryAllocation = table.Column<string>(type: "jsonb", nullable: true),
                    EarlyEntryClose = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    IsShiftBrowsingOpen = table.Column<bool>(type: "boolean", nullable: false),
                    GlobalVolunteerCap = table.Column<int>(type: "integer", nullable: true),
                    ReminderLeadTimeHours = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rotas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventSettingsId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Priority = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Policy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rotas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rotas_event_settings_EventSettingsId",
                        column: x => x.EventSettingsId,
                        principalTable: "event_settings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rotas_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "volunteer_event_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventSettingsId = table.Column<Guid>(type: "uuid", nullable: false),
                    Skills = table.Column<string>(type: "jsonb", nullable: false),
                    Quirks = table.Column<string>(type: "jsonb", nullable: false),
                    Languages = table.Column<string>(type: "jsonb", nullable: false),
                    DietaryPreference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Allergies = table.Column<string>(type: "jsonb", nullable: false),
                    Intolerances = table.Column<string>(type: "jsonb", nullable: false),
                    MedicalConditions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SuppressScheduleChangeEmails = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_event_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volunteer_event_profiles_event_settings_EventSettingsId",
                        column: x => x.EventSettingsId,
                        principalTable: "event_settings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_volunteer_event_profiles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RotaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DayOffset = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<LocalTime>(type: "time", nullable: false),
                    Duration = table.Column<long>(type: "bigint", nullable: false),
                    MinVolunteers = table.Column<int>(type: "integer", nullable: false),
                    MaxVolunteers = table.Column<int>(type: "integer", nullable: false),
                    AdminOnly = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shifts_rotas_RotaId",
                        column: x => x.RotaId,
                        principalTable: "rotas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shift_signups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Enrolled = table.Column<bool>(type: "boolean", nullable: false),
                    EnrolledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    StatusReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_signups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shift_signups_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shift_signups_users_EnrolledByUserId",
                        column: x => x.EnrolledByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_shift_signups_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_shift_signups_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_messages_ShiftSignupId",
                table: "email_outbox_messages",
                column: "ShiftSignupId");

            migrationBuilder.CreateIndex(
                name: "IX_event_settings_IsActive",
                table: "event_settings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_rotas_EventSettingsId_TeamId",
                table: "rotas",
                columns: new[] { "EventSettingsId", "TeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_rotas_TeamId",
                table: "rotas",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_EnrolledByUserId",
                table: "shift_signups",
                column: "EnrolledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_ReviewedByUserId",
                table: "shift_signups",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_ShiftId",
                table: "shift_signups",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_ShiftId_Status",
                table: "shift_signups",
                columns: new[] { "ShiftId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_UserId",
                table: "shift_signups",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_shifts_RotaId",
                table: "shifts",
                column: "RotaId");

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_event_profiles_EventSettingsId",
                table: "volunteer_event_profiles",
                column: "EventSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_event_profiles_UserId_EventSettingsId",
                table: "volunteer_event_profiles",
                columns: new[] { "UserId", "EventSettingsId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_email_outbox_messages_shift_signups_ShiftSignupId",
                table: "email_outbox_messages",
                column: "ShiftSignupId",
                principalTable: "shift_signups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_email_outbox_messages_shift_signups_ShiftSignupId",
                table: "email_outbox_messages");

            migrationBuilder.DropTable(
                name: "shift_signups");

            migrationBuilder.DropTable(
                name: "volunteer_event_profiles");

            migrationBuilder.DropTable(
                name: "shifts");

            migrationBuilder.DropTable(
                name: "rotas");

            migrationBuilder.DropTable(
                name: "event_settings");

            migrationBuilder.DropIndex(
                name: "IX_email_outbox_messages_ShiftSignupId",
                table: "email_outbox_messages");

            migrationBuilder.DropColumn(
                name: "ICalToken",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ShiftSignupId",
                table: "email_outbox_messages");
        }
    }
}
