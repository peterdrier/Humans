using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreSectionForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_store_invoices_store_orders_OrderId",
                table: "store_invoices",
                column: "OrderId",
                principalTable: "store_orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_store_order_lines_store_products_ProductId",
                table: "store_order_lines",
                column: "ProductId",
                principalTable: "store_products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_store_orders_camp_seasons_CampSeasonId",
                table: "store_orders",
                column: "CampSeasonId",
                principalTable: "camp_seasons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_store_invoices_store_orders_OrderId",
                table: "store_invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_store_order_lines_store_products_ProductId",
                table: "store_order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_store_orders_camp_seasons_CampSeasonId",
                table: "store_orders");
        }
    }
}
