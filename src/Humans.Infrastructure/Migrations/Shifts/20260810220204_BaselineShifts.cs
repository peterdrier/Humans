using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Infrastructure.Migrations.Shifts
{
    /// <inheritdoc />
    public partial class BaselineShifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GateOpeningDate = table.Column<LocalDate>(type: "date", nullable: false),
                    BuildStartOffset = table.Column<int>(type: "integer", nullable: false),
                    EventEndOffset = table.Column<int>(type: "integer", nullable: false),
                    StrikeEndOffset = table.Column<int>(type: "integer", nullable: false),
                    FirstCrewStartOffset = table.Column<int>(type: "integer", nullable: false, defaultValue: -25),
                    SetupWeekStartOffset = table.Column<int>(type: "integer", nullable: false, defaultValue: -16),
                    PreEventWeekStartOffset = table.Column<int>(type: "integer", nullable: false, defaultValue: -9),
                    FinishingWeekendStartOffset = table.Column<int>(type: "integer", nullable: false, defaultValue: -4),
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
                name: "volunteer_event_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Skills = table.Column<string>(type: "jsonb", nullable: false),
                    Quirks = table.Column<string>(type: "jsonb", nullable: false),
                    Languages = table.Column<string>(type: "jsonb", nullable: false),
                    DietaryPreference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Allergies = table.Column<string>(type: "jsonb", nullable: false),
                    Intolerances = table.Column<string>(type: "jsonb", nullable: false),
                    AllergyOtherText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IntoleranceOtherText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MedicalConditions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_event_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "general_availability",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventSettingsId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvailableDayOffsets = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_general_availability", x => x.Id);
                    table.ForeignKey(
                        name: "FK_general_availability_event_settings_EventSettingsId",
                        column: x => x.EventSettingsId,
                        principalTable: "event_settings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    Period = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PracticalInfo = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsVisibleToVolunteers = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
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
                });

            migrationBuilder.CreateTable(
                name: "volunteer_build_statuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventSettingsId = table.Column<Guid>(type: "uuid", nullable: false),
                    BarrioSetupStartDate = table.Column<LocalDate>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SetByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SetAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DayOffs = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_build_statuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volunteer_build_statuses_event_settings_EventSettingsId",
                        column: x => x.EventSettingsId,
                        principalTable: "event_settings",
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
                name: "shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RotaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DayOffset = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<LocalTime>(type: "time", nullable: false),
                    Duration = table.Column<long>(type: "bigint", nullable: false),
                    MinVolunteers = table.Column<int>(type: "integer", nullable: false),
                    MaxVolunteers = table.Column<int>(type: "integer", nullable: false),
                    AdminOnly = table.Column<bool>(type: "boolean", nullable: false),
                    IsAllDay = table.Column<bool>(type: "boolean", nullable: false),
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
                    SignupBlockId = table.Column<Guid>(type: "uuid", nullable: true),
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
                });

            migrationBuilder.InsertData(
                table: "shift_tags",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0003-000000000001"), "Heavy lifting" },
                    { new Guid("00000000-0000-0000-0003-000000000002"), "Working in the sun" },
                    { new Guid("00000000-0000-0000-0003-000000000003"), "Working in the shade" },
                    { new Guid("00000000-0000-0000-0003-000000000004"), "Organisational task" },
                    { new Guid("00000000-0000-0000-0003-000000000005"), "Meeting new people" },
                    { new Guid("00000000-0000-0000-0003-000000000006"), "Looking after folks" },
                    { new Guid("00000000-0000-0000-0003-000000000007"), "Exploring the site" },
                    { new Guid("00000000-0000-0000-0003-000000000008"), "Feeding and hydrating folks" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_settings_IsActive",
                table: "event_settings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_general_availability_EventSettingsId",
                table: "general_availability",
                column: "EventSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_general_availability_UserId_EventSettingsId",
                table: "general_availability",
                columns: new[] { "UserId", "EventSettingsId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rota_shift_tags_ShiftTagId",
                table: "rota_shift_tags",
                column: "ShiftTagId");

            migrationBuilder.CreateIndex(
                name: "IX_rotas_EventSettingsId_TeamId",
                table: "rotas",
                columns: new[] { "EventSettingsId", "TeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_ShiftId",
                table: "shift_signups",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_ShiftId_Status",
                table: "shift_signups",
                columns: new[] { "ShiftId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_SignupBlockId",
                table: "shift_signups",
                column: "SignupBlockId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_UserId",
                table: "shift_signups",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_tags_name_unique",
                table: "shift_tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shifts_RotaId",
                table: "shifts",
                column: "RotaId");

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_build_statuses_EventSettingsId",
                table: "volunteer_build_statuses",
                column: "EventSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_build_statuses_UserId_EventSettingsId",
                table: "volunteer_build_statuses",
                columns: new[] { "UserId", "EventSettingsId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_event_profiles_UserId",
                table: "volunteer_event_profiles",
                column: "UserId",
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
                name: "general_availability");

            migrationBuilder.DropTable(
                name: "rota_shift_tags");

            migrationBuilder.DropTable(
                name: "shift_signups");

            migrationBuilder.DropTable(
                name: "volunteer_build_statuses");

            migrationBuilder.DropTable(
                name: "volunteer_event_profiles");

            migrationBuilder.DropTable(
                name: "volunteer_tag_preferences");

            migrationBuilder.DropTable(
                name: "shifts");

            migrationBuilder.DropTable(
                name: "shift_tags");

            migrationBuilder.DropTable(
                name: "rotas");

            migrationBuilder.DropTable(
                name: "event_settings");
        }
    }
}
