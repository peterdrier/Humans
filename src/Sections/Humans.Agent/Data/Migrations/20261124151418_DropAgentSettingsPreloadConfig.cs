using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Agent.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropAgentSettingsPreloadConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreloadConfig",
                table: "agent_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreloadConfig",
                table: "agent_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "agent_settings",
                keyColumn: "Id",
                keyValue: 1,
                column: "PreloadConfig",
                value: "Tier1");
        }
    }
}
