using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Pleo.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_pleo_balance",
                table: "pleo_balance");

            migrationBuilder.RenameTable(
                name: "pleo_balance",
                newName: "pleo_balances");

            migrationBuilder.AddPrimaryKey(
                name: "PK_pleo_balances",
                table: "pleo_balances",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_pleo_balances",
                table: "pleo_balances");

            migrationBuilder.RenameTable(
                name: "pleo_balances",
                newName: "pleo_balance");

            migrationBuilder.AddPrimaryKey(
                name: "PK_pleo_balance",
                table: "pleo_balance",
                column: "Id");
        }
    }
}
