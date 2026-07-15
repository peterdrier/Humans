using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Expenses peel
    /// (nobodies-collective/Humans#858): <c>expense_reports</c>,
    /// <c>expense_lines</c>, <c>expense_attachments</c> and
    /// <c>holded_expense_outbox_events</c> moved to <c>ExpensesDbContext</c>,
    /// which owns the physical tables from here on. The scaffolded
    /// <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately emptied —
    /// Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
