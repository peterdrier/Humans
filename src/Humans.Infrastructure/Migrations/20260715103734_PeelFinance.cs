using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Finance peel
    /// (nobodies-collective/Humans#858): <c>holded_expense_docs</c>,
    /// <c>holded_category_map</c>, <c>holded_ledger_lines</c>,
    /// <c>holded_creditor_contacts</c> and <c>holded_sync_states</c> moved to
    /// <c>FinanceDbContext</c>, which owns the physical tables from here on. The
    /// scaffolded <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately
    /// emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelFinance : Migration
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
