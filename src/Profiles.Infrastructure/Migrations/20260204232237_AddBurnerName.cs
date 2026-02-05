using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Profiles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBurnerName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BurnerName",
                table: "profiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BurnerName",
                table: "profiles");
        }
    }
}
