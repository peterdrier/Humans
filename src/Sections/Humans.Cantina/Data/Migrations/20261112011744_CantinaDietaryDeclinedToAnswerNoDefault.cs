using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Cantina.Data.Migrations
{
    /// <inheritdoc />
    public partial class CantinaDietaryDeclinedToAnswerNoDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "DeclinedToAnswer",
                table: "cantina_dietary_profiles",
                type: "boolean",
                nullable: true,
                defaultValueSql: "NULL",
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "DeclinedToAnswer",
                table: "cantina_dietary_profiles",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true,
                oldDefaultValueSql: "NULL");
        }
    }
}
