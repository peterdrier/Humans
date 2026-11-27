using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Store.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreCheckoutIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckoutIntent",
                table: "store_orders",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckoutIntent",
                table: "store_orders");
        }
    }
}
