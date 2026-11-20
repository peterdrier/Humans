using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileChildTablesUserForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_user_contact_fields_users_UserId",
                table: "user_contact_fields",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_profile_languages_users_UserId",
                table: "user_profile_languages",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_volunteer_history_entries_users_UserId",
                table: "user_volunteer_history_entries",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_contact_fields_users_UserId",
                table: "user_contact_fields");

            migrationBuilder.DropForeignKey(
                name: "FK_user_profile_languages_users_UserId",
                table: "user_profile_languages");

            migrationBuilder.DropForeignKey(
                name: "FK_user_volunteer_history_entries_users_UserId",
                table: "user_volunteer_history_entries");
        }
    }
}
