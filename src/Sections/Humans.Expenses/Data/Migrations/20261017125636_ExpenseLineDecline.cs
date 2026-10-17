using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Expenses.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpenseLineDecline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "DeclinedAt",
                table: "expense_lines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeclinedByUserId",
                table: "expense_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeclinedReason",
                table: "expense_lines",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeclinedAt",
                table: "expense_lines");

            migrationBuilder.DropColumn(
                name: "DeclinedByUserId",
                table: "expense_lines");

            migrationBuilder.DropColumn(
                name: "DeclinedReason",
                table: "expense_lines");
        }
    }
}
