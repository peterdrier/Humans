using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Workgroups.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_workgroups_settings",
                table: "workgroups_settings");

            migrationBuilder.RenameTable(
                name: "workgroups_settings",
                newName: "workgroup_settings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workgroup_settings",
                table: "workgroup_settings",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_workgroup_settings",
                table: "workgroup_settings");

            migrationBuilder.RenameTable(
                name: "workgroup_settings",
                newName: "workgroups_settings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workgroups_settings",
                table: "workgroups_settings",
                column: "Id");
        }
    }
}
