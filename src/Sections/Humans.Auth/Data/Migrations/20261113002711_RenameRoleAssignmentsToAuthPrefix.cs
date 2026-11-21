using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameRoleAssignmentsToAuthPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_role_assignments",
                table: "role_assignments");

            migrationBuilder.RenameTable(
                name: "role_assignments",
                newName: "auth_role_assignments");

            migrationBuilder.RenameIndex(
                name: "IX_role_assignments_UserId_RoleName_ValidFrom",
                table: "auth_role_assignments",
                newName: "IX_auth_role_assignments_UserId_RoleName_ValidFrom");

            migrationBuilder.RenameIndex(
                name: "IX_role_assignments_UserId_RoleName",
                table: "auth_role_assignments",
                newName: "IX_auth_role_assignments_UserId_RoleName");

            migrationBuilder.RenameIndex(
                name: "IX_role_assignments_UserId",
                table: "auth_role_assignments",
                newName: "IX_auth_role_assignments_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_role_assignments_RoleName",
                table: "auth_role_assignments",
                newName: "IX_auth_role_assignments_RoleName");

            migrationBuilder.AddPrimaryKey(
                name: "PK_auth_role_assignments",
                table: "auth_role_assignments",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_auth_role_assignments",
                table: "auth_role_assignments");

            migrationBuilder.RenameTable(
                name: "auth_role_assignments",
                newName: "role_assignments");

            migrationBuilder.RenameIndex(
                name: "IX_auth_role_assignments_UserId_RoleName_ValidFrom",
                table: "role_assignments",
                newName: "IX_role_assignments_UserId_RoleName_ValidFrom");

            migrationBuilder.RenameIndex(
                name: "IX_auth_role_assignments_UserId_RoleName",
                table: "role_assignments",
                newName: "IX_role_assignments_UserId_RoleName");

            migrationBuilder.RenameIndex(
                name: "IX_auth_role_assignments_UserId",
                table: "role_assignments",
                newName: "IX_role_assignments_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_auth_role_assignments_RoleName",
                table: "role_assignments",
                newName: "IX_role_assignments_RoleName");

            migrationBuilder.AddPrimaryKey(
                name: "PK_role_assignments",
                table: "role_assignments",
                column: "Id");
        }
    }
}
