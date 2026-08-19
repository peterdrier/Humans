using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Expenses.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpenseLineProofRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentLineId",
                table: "expense_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_expense_lines_ParentLineId",
                table: "expense_lines",
                column: "ParentLineId");

            migrationBuilder.AddForeignKey(
                name: "FK_expense_lines_expense_lines_ParentLineId",
                table: "expense_lines",
                column: "ParentLineId",
                principalTable: "expense_lines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_expense_lines_expense_lines_ParentLineId",
                table: "expense_lines");

            migrationBuilder.DropIndex(
                name: "IX_expense_lines_ParentLineId",
                table: "expense_lines");

            migrationBuilder.DropColumn(
                name: "ParentLineId",
                table: "expense_lines");
        }
    }
}
