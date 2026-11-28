using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropRetiredChildPriceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChildMaxAge",
                table: "ticket_sales_events");

            migrationBuilder.DropColumn(
                name: "ChildPriceMinor",
                table: "ticket_sales_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChildMaxAge",
                table: "ticket_sales_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ChildPriceMinor",
                table: "ticket_sales_events",
                type: "bigint",
                nullable: true);
        }
    }
}
