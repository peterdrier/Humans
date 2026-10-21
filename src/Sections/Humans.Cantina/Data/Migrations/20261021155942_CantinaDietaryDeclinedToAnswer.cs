using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Cantina.Data.Migrations
{
    /// <inheritdoc />
    public partial class CantinaDietaryDeclinedToAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeclinedToAnswer",
                table: "cantina_dietary_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeclinedToAnswer",
                table: "cantina_dietary_profiles");
        }
    }
}
