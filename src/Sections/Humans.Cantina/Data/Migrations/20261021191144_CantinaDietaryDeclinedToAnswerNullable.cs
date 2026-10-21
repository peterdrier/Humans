using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Cantina.Data.Migrations
{
    /// <inheritdoc />
    public partial class CantinaDietaryDeclinedToAnswerNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "DeclinedToAnswer",
                table: "cantina_dietary_profiles",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "DeclinedToAnswer",
                table: "cantina_dietary_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }
    }
}
