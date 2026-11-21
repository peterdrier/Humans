using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropProfileChildTablesProfileId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_contact_fields_user_profiles_ProfileId",
                table: "user_contact_fields");

            migrationBuilder.DropForeignKey(
                name: "FK_user_profile_languages_user_profiles_ProfileId",
                table: "user_profile_languages");

            migrationBuilder.DropForeignKey(
                name: "FK_user_volunteer_history_entries_user_profiles_ProfileId",
                table: "user_volunteer_history_entries");

            migrationBuilder.DropIndex(
                name: "IX_user_volunteer_history_entries_ProfileId",
                table: "user_volunteer_history_entries");

            migrationBuilder.DropIndex(
                name: "IX_user_profile_languages_ProfileId",
                table: "user_profile_languages");

            migrationBuilder.DropIndex(
                name: "IX_user_contact_fields_ProfileId",
                table: "user_contact_fields");

            migrationBuilder.DropIndex(
                name: "IX_user_contact_fields_ProfileId_Visibility",
                table: "user_contact_fields");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "user_volunteer_history_entries");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "user_profile_languages");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "user_contact_fields");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "user_volunteer_history_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "user_profile_languages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "user_contact_fields",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_user_volunteer_history_entries_ProfileId",
                table: "user_volunteer_history_entries",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_user_profile_languages_ProfileId",
                table: "user_profile_languages",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_user_contact_fields_ProfileId",
                table: "user_contact_fields",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_user_contact_fields_ProfileId_Visibility",
                table: "user_contact_fields",
                columns: new[] { "ProfileId", "Visibility" });

            migrationBuilder.AddForeignKey(
                name: "FK_user_contact_fields_user_profiles_ProfileId",
                table: "user_contact_fields",
                column: "ProfileId",
                principalTable: "user_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_profile_languages_user_profiles_ProfileId",
                table: "user_profile_languages",
                column: "ProfileId",
                principalTable: "user_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_volunteer_history_entries_user_profiles_ProfileId",
                table: "user_volunteer_history_entries",
                column: "ProfileId",
                principalTable: "user_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
