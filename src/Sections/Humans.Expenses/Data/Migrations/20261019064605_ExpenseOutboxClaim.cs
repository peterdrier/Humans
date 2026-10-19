using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Expenses.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpenseOutboxClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "ClaimedAt",
                table: "holded_expense_outbox_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimedBy",
                table: "holded_expense_outbox_events",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "holded_expense_outbox_events");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "holded_expense_outbox_events");
        }
    }
}
