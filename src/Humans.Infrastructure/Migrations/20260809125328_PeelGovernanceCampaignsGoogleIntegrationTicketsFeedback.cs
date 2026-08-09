using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Governance, Campaigns, GoogleIntegration,
    /// Tickets and Feedback peels (nobodies-collective/Humans#858):
    /// <c>applications</c>, <c>application_state_history</c>, <c>board_votes</c>,
    /// <c>campaigns</c>, <c>campaign_codes</c>, <c>campaign_grants</c>,
    /// <c>google_resources</c>, <c>google_sync_outbox</c>,
    /// <c>sync_service_settings</c>, <c>ticket_orders</c>,
    /// <c>ticket_attendees</c>, <c>ticket_sync_state</c>,
    /// <c>ticket_transfer_requests</c>, <c>feedback_reports</c> and
    /// <c>feedback_messages</c> moved to <c>GovernanceDbContext</c>,
    /// <c>CampaignsDbContext</c>, <c>GoogleIntegrationDbContext</c>,
    /// <c>TicketsDbContext</c> and <c>FeedbackDbContext</c>, which own the
    /// physical tables from here on. The scaffolded
    /// <c>DropTable</c>/<c>CreateTable</c>/<c>InsertData</c> bodies were
    /// deliberately emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema
    /// and re-seeds nothing.
    /// </summary>
    public partial class PeelGovernanceCampaignsGoogleIntegrationTicketsFeedback : Migration
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
