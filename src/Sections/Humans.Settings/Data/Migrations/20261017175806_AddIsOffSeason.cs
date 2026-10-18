using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Settings.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsOffSeason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOffSeason",
                table: "settings_event",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsOffSeason",
                table: "settings_event");
        }
    }
}
