using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Store.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreProductExpenseAndRechargeAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HoldedExpenseAccountNum",
                table: "store_products",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HoldedRechargeAccountNum",
                table: "store_products",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HoldedExpenseAccountNum",
                table: "store_products");

            migrationBuilder.DropColumn(
                name: "HoldedRechargeAccountNum",
                table: "store_products");
        }
    }
}
