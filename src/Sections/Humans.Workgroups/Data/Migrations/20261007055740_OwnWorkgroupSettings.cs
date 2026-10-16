using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Workgroups.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnWorkgroupSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workgroups_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RootDriveFolderId = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workgroups_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workgroups_settings");
        }
    }
}
