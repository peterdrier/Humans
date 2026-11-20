using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Camps.Data.Migrations
{
    /// <inheritdoc />
    public partial class CampEventRegistrationInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RegistrationInfo",
                table: "camp_event_settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegistrationInfo",
                table: "camp_event_settings");
        }
    }
}
