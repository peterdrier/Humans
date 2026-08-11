using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoldedExpenseDocIsApproved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "holded_expense_docs");

            migrationBuilder.AddColumn<bool>(
                name: "IsApproved",
                table: "holded_expense_docs",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsApproved",
                table: "holded_expense_docs");

            migrationBuilder.AddColumn<Instant>(
                name: "ApprovedAt",
                table: "holded_expense_docs",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
