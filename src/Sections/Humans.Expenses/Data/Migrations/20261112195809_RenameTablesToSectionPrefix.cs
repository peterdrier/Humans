using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Expenses.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_holded_expense_outbox_events",
                table: "holded_expense_outbox_events");

            migrationBuilder.RenameTable(
                name: "holded_expense_outbox_events",
                newName: "expense_holded_outbox_events");

            migrationBuilder.RenameIndex(
                name: "IX_holded_expense_outbox_events_ProcessedAt_FailedPermanently",
                table: "expense_holded_outbox_events",
                newName: "IX_expense_holded_outbox_events_ProcessedAt_FailedPermanently");

            migrationBuilder.RenameIndex(
                name: "IX_holded_expense_outbox_events_ExpenseReportId",
                table: "expense_holded_outbox_events",
                newName: "IX_expense_holded_outbox_events_ExpenseReportId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_expense_holded_outbox_events",
                table: "expense_holded_outbox_events",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_expense_holded_outbox_events",
                table: "expense_holded_outbox_events");

            migrationBuilder.RenameTable(
                name: "expense_holded_outbox_events",
                newName: "holded_expense_outbox_events");

            migrationBuilder.RenameIndex(
                name: "IX_expense_holded_outbox_events_ProcessedAt_FailedPermanently",
                table: "holded_expense_outbox_events",
                newName: "IX_holded_expense_outbox_events_ProcessedAt_FailedPermanently");

            migrationBuilder.RenameIndex(
                name: "IX_expense_holded_outbox_events_ExpenseReportId",
                table: "holded_expense_outbox_events",
                newName: "IX_holded_expense_outbox_events_ExpenseReportId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_holded_expense_outbox_events",
                table: "holded_expense_outbox_events",
                column: "Id");
        }
    }
}
