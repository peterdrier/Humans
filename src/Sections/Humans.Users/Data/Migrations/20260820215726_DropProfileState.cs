using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropProfileState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "State",
                table: "profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "profiles",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
