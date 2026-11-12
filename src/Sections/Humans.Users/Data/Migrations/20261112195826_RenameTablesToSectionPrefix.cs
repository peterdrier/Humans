using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_account_merge_requests_users_ResolvedByUserId",
                table: "account_merge_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_account_merge_requests_users_SourceUserId",
                table: "account_merge_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_account_merge_requests_users_TargetUserId",
                table: "account_merge_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_contact_fields_profiles_ProfileId",
                table: "contact_fields");

            migrationBuilder.DropForeignKey(
                name: "FK_profile_languages_profiles_ProfileId",
                table: "profile_languages");

            migrationBuilder.DropForeignKey(
                name: "FK_profiles_users_UserId",
                table: "profiles");

            migrationBuilder.DropForeignKey(
                name: "FK_volunteer_history_entries_profiles_ProfileId",
                table: "volunteer_history_entries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_volunteer_history_entries",
                table: "volunteer_history_entries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_profiles",
                table: "profiles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_profile_languages",
                table: "profile_languages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_contact_fields",
                table: "contact_fields");

            migrationBuilder.DropPrimaryKey(
                name: "PK_account_merge_requests",
                table: "account_merge_requests");

            migrationBuilder.RenameTable(
                name: "volunteer_history_entries",
                newName: "user_volunteer_history_entries");

            migrationBuilder.RenameTable(
                name: "profiles",
                newName: "user_profiles");

            migrationBuilder.RenameTable(
                name: "profile_languages",
                newName: "user_profile_languages");

            migrationBuilder.RenameTable(
                name: "contact_fields",
                newName: "user_contact_fields");

            migrationBuilder.RenameTable(
                name: "account_merge_requests",
                newName: "user_account_merge_requests");

            migrationBuilder.RenameIndex(
                name: "IX_volunteer_history_entries_ProfileId",
                table: "user_volunteer_history_entries",
                newName: "IX_user_volunteer_history_entries_ProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_profiles_UserId",
                table: "user_profiles",
                newName: "IX_user_profiles_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_profile_languages_ProfileId",
                table: "user_profile_languages",
                newName: "IX_user_profile_languages_ProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_contact_fields_ProfileId_Visibility",
                table: "user_contact_fields",
                newName: "IX_user_contact_fields_ProfileId_Visibility");

            migrationBuilder.RenameIndex(
                name: "IX_contact_fields_ProfileId",
                table: "user_contact_fields",
                newName: "IX_user_contact_fields_ProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_account_merge_requests_TargetUserId",
                table: "user_account_merge_requests",
                newName: "IX_user_account_merge_requests_TargetUserId");

            migrationBuilder.RenameIndex(
                name: "IX_account_merge_requests_Status",
                table: "user_account_merge_requests",
                newName: "IX_user_account_merge_requests_Status");

            migrationBuilder.RenameIndex(
                name: "IX_account_merge_requests_SourceUserId",
                table: "user_account_merge_requests",
                newName: "IX_user_account_merge_requests_SourceUserId");

            migrationBuilder.RenameIndex(
                name: "IX_account_merge_requests_ResolvedByUserId",
                table: "user_account_merge_requests",
                newName: "IX_user_account_merge_requests_ResolvedByUserId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_volunteer_history_entries",
                table: "user_volunteer_history_entries",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_profiles",
                table: "user_profiles",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_profile_languages",
                table: "user_profile_languages",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_contact_fields",
                table: "user_contact_fields",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_account_merge_requests",
                table: "user_account_merge_requests",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_user_account_merge_requests_users_ResolvedByUserId",
                table: "user_account_merge_requests",
                column: "ResolvedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_user_account_merge_requests_users_SourceUserId",
                table: "user_account_merge_requests",
                column: "SourceUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_account_merge_requests_users_TargetUserId",
                table: "user_account_merge_requests",
                column: "TargetUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

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
                name: "FK_user_profiles_users_UserId",
                table: "user_profiles",
                column: "UserId",
                principalTable: "users",
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_account_merge_requests_users_ResolvedByUserId",
                table: "user_account_merge_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_user_account_merge_requests_users_SourceUserId",
                table: "user_account_merge_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_user_account_merge_requests_users_TargetUserId",
                table: "user_account_merge_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_user_contact_fields_user_profiles_ProfileId",
                table: "user_contact_fields");

            migrationBuilder.DropForeignKey(
                name: "FK_user_profile_languages_user_profiles_ProfileId",
                table: "user_profile_languages");

            migrationBuilder.DropForeignKey(
                name: "FK_user_profiles_users_UserId",
                table: "user_profiles");

            migrationBuilder.DropForeignKey(
                name: "FK_user_volunteer_history_entries_user_profiles_ProfileId",
                table: "user_volunteer_history_entries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_volunteer_history_entries",
                table: "user_volunteer_history_entries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_profiles",
                table: "user_profiles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_profile_languages",
                table: "user_profile_languages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_contact_fields",
                table: "user_contact_fields");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_account_merge_requests",
                table: "user_account_merge_requests");

            migrationBuilder.RenameTable(
                name: "user_volunteer_history_entries",
                newName: "volunteer_history_entries");

            migrationBuilder.RenameTable(
                name: "user_profiles",
                newName: "profiles");

            migrationBuilder.RenameTable(
                name: "user_profile_languages",
                newName: "profile_languages");

            migrationBuilder.RenameTable(
                name: "user_contact_fields",
                newName: "contact_fields");

            migrationBuilder.RenameTable(
                name: "user_account_merge_requests",
                newName: "account_merge_requests");

            migrationBuilder.RenameIndex(
                name: "IX_user_volunteer_history_entries_ProfileId",
                table: "volunteer_history_entries",
                newName: "IX_volunteer_history_entries_ProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_user_profiles_UserId",
                table: "profiles",
                newName: "IX_profiles_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_user_profile_languages_ProfileId",
                table: "profile_languages",
                newName: "IX_profile_languages_ProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_user_contact_fields_ProfileId_Visibility",
                table: "contact_fields",
                newName: "IX_contact_fields_ProfileId_Visibility");

            migrationBuilder.RenameIndex(
                name: "IX_user_contact_fields_ProfileId",
                table: "contact_fields",
                newName: "IX_contact_fields_ProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_user_account_merge_requests_TargetUserId",
                table: "account_merge_requests",
                newName: "IX_account_merge_requests_TargetUserId");

            migrationBuilder.RenameIndex(
                name: "IX_user_account_merge_requests_Status",
                table: "account_merge_requests",
                newName: "IX_account_merge_requests_Status");

            migrationBuilder.RenameIndex(
                name: "IX_user_account_merge_requests_SourceUserId",
                table: "account_merge_requests",
                newName: "IX_account_merge_requests_SourceUserId");

            migrationBuilder.RenameIndex(
                name: "IX_user_account_merge_requests_ResolvedByUserId",
                table: "account_merge_requests",
                newName: "IX_account_merge_requests_ResolvedByUserId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_volunteer_history_entries",
                table: "volunteer_history_entries",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_profiles",
                table: "profiles",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_profile_languages",
                table: "profile_languages",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_contact_fields",
                table: "contact_fields",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_account_merge_requests",
                table: "account_merge_requests",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_account_merge_requests_users_ResolvedByUserId",
                table: "account_merge_requests",
                column: "ResolvedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_account_merge_requests_users_SourceUserId",
                table: "account_merge_requests",
                column: "SourceUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_account_merge_requests_users_TargetUserId",
                table: "account_merge_requests",
                column: "TargetUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_contact_fields_profiles_ProfileId",
                table: "contact_fields",
                column: "ProfileId",
                principalTable: "profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_profile_languages_profiles_ProfileId",
                table: "profile_languages",
                column: "ProfileId",
                principalTable: "profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_profiles_users_UserId",
                table: "profiles",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_volunteer_history_entries_profiles_ProfileId",
                table: "volunteer_history_entries",
                column: "ProfileId",
                principalTable: "profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
