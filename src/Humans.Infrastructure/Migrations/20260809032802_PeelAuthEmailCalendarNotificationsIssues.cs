using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Auth, Email, Calendar, Notifications and
    /// Issues peels (nobodies-collective/Humans#858): <c>role_assignments</c>,
    /// <c>email_outbox_messages</c>, <c>calendar_events</c>,
    /// <c>calendar_event_exceptions</c>, <c>notifications</c>,
    /// <c>notification_recipients</c>, <c>issues</c> and <c>issue_comments</c>
    /// moved to <c>AuthDbContext</c>, <c>EmailDbContext</c>,
    /// <c>CalendarDbContext</c>, <c>NotificationsDbContext</c> and
    /// <c>IssuesDbContext</c>, which own the physical tables from here on. The
    /// scaffolded <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately
    /// emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelAuthEmailCalendarNotificationsIssues : Migration
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
