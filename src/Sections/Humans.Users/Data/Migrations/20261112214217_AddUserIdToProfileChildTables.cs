using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIdToProfileChildTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "volunteer_history_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "profile_languages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "contact_fields",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_history_entries_UserId",
                table: "volunteer_history_entries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_profile_languages_UserId",
                table: "profile_languages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_contact_fields_UserId",
                table: "contact_fields",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_volunteer_history_entries_UserId",
                table: "volunteer_history_entries");

            migrationBuilder.DropIndex(
                name: "IX_profile_languages_UserId",
                table: "profile_languages");

            migrationBuilder.DropIndex(
                name: "IX_contact_fields_UserId",
                table: "contact_fields");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "volunteer_history_entries");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "profile_languages");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "contact_fields");
        }
    }
}
