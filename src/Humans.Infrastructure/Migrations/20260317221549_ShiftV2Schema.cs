using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ShiftV2Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "shifts");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "shifts");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "rotas");

            migrationBuilder.AddColumn<string>(
                name: "Period",
                table: "team_role_definitions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "YearRound");

            migrationBuilder.AddColumn<bool>(
                name: "IsAllDay",
                table: "shifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SignupBlockId",
                table: "shift_signups",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Period",
                table: "rotas",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Event");

            migrationBuilder.AddColumn<string>(
                name: "PracticalInfo",
                table: "rotas",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

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
                    table.ForeignKey(
                        name: "FK_general_availability_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_SignupBlockId",
                table: "shift_signups",
                column: "SignupBlockId");

            migrationBuilder.CreateIndex(
                name: "IX_general_availability_EventSettingsId",
                table: "general_availability",
                column: "EventSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_general_availability_UserId_EventSettingsId",
                table: "general_availability",
                columns: new[] { "UserId", "EventSettingsId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "general_availability");

            migrationBuilder.DropIndex(
                name: "IX_shift_signups_SignupBlockId",
                table: "shift_signups");

            migrationBuilder.DropColumn(
                name: "Period",
                table: "team_role_definitions");

            migrationBuilder.DropColumn(
                name: "IsAllDay",
                table: "shifts");

            migrationBuilder.DropColumn(
                name: "SignupBlockId",
                table: "shift_signups");

            migrationBuilder.DropColumn(
                name: "Period",
                table: "rotas");

            migrationBuilder.DropColumn(
                name: "PracticalInfo",
                table: "rotas");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "shifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "shifts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "rotas",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
