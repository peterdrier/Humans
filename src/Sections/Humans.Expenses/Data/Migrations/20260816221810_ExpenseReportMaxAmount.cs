using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Expenses.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpenseReportMaxAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MaxAmount",
                table: "expense_reports",
                type: "numeric(12,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxAmount",
                table: "expense_reports");
        }
    }
}
