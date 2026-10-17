using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Expenses.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpenseReportPreapprovalGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PreapprovalGrantId",
                table: "expense_reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_expense_reports_PreapprovalGrantId",
                table: "expense_reports",
                column: "PreapprovalGrantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_expense_reports_PreapprovalGrantId",
                table: "expense_reports");

            migrationBuilder.DropColumn(
                name: "PreapprovalGrantId",
                table: "expense_reports");
        }
    }
}
