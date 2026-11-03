using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Workgroups.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkgroupRecurringMeetingsAndNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecurrenceRule",
                table: "workgroup_meetings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceTimezone",
                table: "workgroup_meetings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MeetingId",
                table: "workgroup_log_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_log_entries_MeetingId",
                table: "workgroup_log_entries",
                column: "MeetingId");

            migrationBuilder.AddForeignKey(
                name: "FK_workgroup_log_entries_workgroup_meetings_MeetingId",
                table: "workgroup_log_entries",
                column: "MeetingId",
                principalTable: "workgroup_meetings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workgroup_log_entries_workgroup_meetings_MeetingId",
                table: "workgroup_log_entries");

            migrationBuilder.DropIndex(
                name: "IX_workgroup_log_entries_MeetingId",
                table: "workgroup_log_entries");

            migrationBuilder.DropColumn(
                name: "RecurrenceRule",
                table: "workgroup_meetings");

            migrationBuilder.DropColumn(
                name: "RecurrenceTimezone",
                table: "workgroup_meetings");

            migrationBuilder.DropColumn(
                name: "MeetingId",
                table: "workgroup_log_entries");
        }
    }
}
