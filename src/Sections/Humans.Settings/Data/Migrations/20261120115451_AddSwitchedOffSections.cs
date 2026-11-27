using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Settings.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSwitchedOffSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SwitchedOffSections",
                table: "settings_events",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SwitchedOffSections",
                table: "settings_events");
        }
    }
}
