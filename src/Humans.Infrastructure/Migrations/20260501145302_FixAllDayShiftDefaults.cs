using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixAllDayShiftDefaults : Migration
    {
        // Data fix only — no schema changes.
        // Existing all-day shifts were seeded with StartTime=00:00 / Duration=24h (86400s),
        // which caused overnight shifts ending at e.g. 02:00 to falsely conflict with the
        // following day's "all-day" shift. New all-day shifts are now seeded as 08:00 / 10h.
        // This migration retroactively converts existing rows that still match the old
        // 24h default — leaving any hand-edited rows untouched.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE shifts
                SET "StartTime" = TIME '08:00:00',
                    "Duration" = 36000
                WHERE "IsAllDay" = true
                  AND "StartTime" = TIME '00:00:00'
                  AND "Duration" = 86400;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE shifts
                SET "StartTime" = TIME '00:00:00',
                    "Duration" = 86400
                WHERE "IsAllDay" = true
                  AND "StartTime" = TIME '08:00:00'
                  AND "Duration" = 36000;
                """);
        }
    }
}
