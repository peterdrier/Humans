using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Store peel
    /// (nobodies-collective/Humans#858): <c>store_products</c>,
    /// <c>store_orders</c>, <c>store_order_lines</c>, <c>store_payments</c>,
    /// <c>store_invoices</c> and <c>store_treasury_sync_state</c> moved to
    /// <c>StoreDbContext</c>, which owns the physical tables from here on. The
    /// scaffolded <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately
    /// emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelStore : Migration
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
