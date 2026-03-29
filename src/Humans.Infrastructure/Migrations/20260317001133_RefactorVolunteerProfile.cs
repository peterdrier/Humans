using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefactorVolunteerProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_volunteer_event_profiles_event_settings_EventSettingsId",
                table: "volunteer_event_profiles");

            migrationBuilder.DropIndex(
                name: "IX_volunteer_event_profiles_EventSettingsId",
                table: "volunteer_event_profiles");

            migrationBuilder.DropIndex(
                name: "IX_volunteer_event_profiles_UserId_EventSettingsId",
                table: "volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "SuppressScheduleChangeEmails",
                table: "volunteer_event_profiles");

            migrationBuilder.AddColumn<bool>(
                name: "SuppressScheduleChangeEmails",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_event_profiles_UserId",
                table: "volunteer_event_profiles",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_volunteer_event_profiles_UserId",
                table: "volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "SuppressScheduleChangeEmails",
                table: "users");

            migrationBuilder.AddColumn<Guid>(
                name: "EventSettingsId",
                table: "volunteer_event_profiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "SuppressScheduleChangeEmails",
                table: "volunteer_event_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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
                name: "FK_volunteer_event_profiles_event_settings_EventSettingsId",
                table: "volunteer_event_profiles",
                column: "EventSettingsId",
                principalTable: "event_settings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
