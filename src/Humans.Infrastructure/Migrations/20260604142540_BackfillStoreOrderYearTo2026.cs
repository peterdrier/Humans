using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillStoreOrderYearTo2026 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Year column was added (20260527214625) with defaultValue 0 and no backfill,
            // so store_orders rows that predate it carry Year = 0. The org runs a single event
            // year, so these are all 2026. Set them so order reads/repricing use the real catalog
            // year instead of relying on scattered Year == 0 fallbacks (nobodies-collective/Humans#816).
            migrationBuilder.Sql("""UPDATE store_orders SET "Year" = 2026 WHERE "Year" = 0""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No rollback — we cannot distinguish which rows were previously Year = 0.
        }
    }
}
