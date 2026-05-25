using Hangfire;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Jobs;

namespace Humans.Web.Extensions;

public static class RecurringJobExtensions
{
    public static void UseHumansRecurringJobs(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RecurringJobExtensions));

        var registry = app.Services.GetRequiredService<ConfigurationRegistry>();
        var ticketSyncInterval = app.Configuration.GetSettingValue(
            registry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor", defaultValue: 15);
        // MailerLite:AudienceSyncCron is opt-in. When empty/unset, the recurring
        // job is not registered — admins still trigger syncs on demand via the
        // /Mailer/Admin "Push Now" button. Set to e.g. "0 6 * * *" to enable.
        var mailerAudienceCron = app.Configuration.GetValue<string>("MailerLite:AudienceSyncCron")
            ?? string.Empty;

        var jobs = new (string Id, Action Register)[]
        {
            // Google sync jobs — controlled by SyncServiceSettings (Admin/SyncSettings).
            // Set service mode to "None" to disable without redeploying.
            ("system-team-sync", () => RecurringJob.AddOrUpdate<ISystemTeamSync>(
                "system-team-sync", job => job.ExecuteAsync(CancellationToken.None), Cron.Hourly)),

            ("google-resource-reconciliation", () => RecurringJob.AddOrUpdate<GoogleResourceReconciliationJob>(
                "google-resource-reconciliation", job => job.ExecuteAsync(CancellationToken.None), "0 3 * * *")),

            ("process-account-deletions", () => RecurringJob.AddOrUpdate<ProcessAccountDeletionsJob>(
                "process-account-deletions", job => job.ExecuteAsync(CancellationToken.None), Cron.Daily)),

            ("legal-document-sync", () => RecurringJob.AddOrUpdate<SyncLegalDocumentsJob>(
                "legal-document-sync", job => job.ExecuteAsync(CancellationToken.None), "0 4 * * *")),

            ("suspend-non-compliant-members", () => RecurringJob.AddOrUpdate<SuspendNonCompliantMembersJob>(
                "suspend-non-compliant-members", job => job.ExecuteAsync(CancellationToken.None), "30 4 * * *")),

            // Send re-consent reminders before suspension job runs.
            // Runs daily at 04:00, 30 minutes before SuspendNonCompliantMembersJob.
            ("send-reconsent-reminders", () => RecurringJob.AddOrUpdate<SendReConsentReminderJob>(
                "send-reconsent-reminders", job => job.ExecuteAsync(CancellationToken.None), "0 4 * * *")),

            ("process-google-sync-outbox", () => RecurringJob.AddOrUpdate<ProcessGoogleSyncOutboxJob>(
                "process-google-sync-outbox", job => job.ExecuteAsync(CancellationToken.None), "*/10 * * * *")),

            ("drive-activity-monitor", () => RecurringJob.AddOrUpdate<DriveActivityMonitorJob>(
                "drive-activity-monitor", job => job.ExecuteAsync(CancellationToken.None), Cron.Hourly)),

            // Send term renewal reminders to Colaboradors/Asociados whose terms expire within 90 days.
            ("term-renewal-reminder", () => RecurringJob.AddOrUpdate<TermRenewalReminderJob>(
                "term-renewal-reminder", job => job.ExecuteAsync(CancellationToken.None), "0 5 * * 1")),

            ("process-email-outbox", () => RecurringJob.AddOrUpdate<ProcessEmailOutboxJob>(
                "process-email-outbox", job => job.ExecuteAsync(CancellationToken.None), "*/1 * * * *")),

            ("cleanup-email-outbox", () => RecurringJob.AddOrUpdate<CleanupEmailOutboxJob>(
                "cleanup-email-outbox", job => job.ExecuteAsync(CancellationToken.None), "0 3 * * 0")),

            // Clean up resolved notifications older than 7 days — daily at 04:30 UTC.
            ("cleanup-notifications", () => RecurringJob.AddOrUpdate<CleanupNotificationsJob>(
                "cleanup-notifications", job => job.ExecuteAsync(CancellationToken.None), "30 4 * * *")),

            // Clean up issues 6 months after they entered a terminal state — daily at 05:00 UTC.
            ("cleanup-issues", () => RecurringJob.AddOrUpdate<CleanupIssuesJob>(
                "cleanup-issues", job => job.ExecuteAsync(CancellationToken.None), "0 5 * * *")),

            // Sync ticket data from vendor at configured interval (default 15 min).
            ("ticket-vendor-sync", () => RecurringJob.AddOrUpdate<TicketSyncJob>(
                "ticket-vendor-sync", job => job.ExecuteAsync(CancellationToken.None), $"*/{ticketSyncInterval} * * * *")),

            // Materialize ticket sales actuals into budget line items daily at 04:30.
            ("ticketing-budget-sync", () => RecurringJob.AddOrUpdate<TicketingBudgetSyncJob>(
                "ticketing-budget-sync", job => job.ExecuteAsync(CancellationToken.None), "30 4 * * *")),

            // Push approved expense reports to Holded as purchase documents — every minute.
            ("holded-expense-outbox", () => RecurringJob.AddOrUpdate<HoldedExpenseOutboxJob>(
                "holded-expense-outbox", job => job.ExecuteAsync(CancellationToken.None), "*/1 * * * *")),

            // Poll Holded for payment confirmation on SepaSent reports — every 6 hours.
            ("expense-paid-polling", () => RecurringJob.AddOrUpdate<ExpensePaidPollingJob>(
                "expense-paid-polling", job => job.ExecuteAsync(CancellationToken.None), "0 */6 * * *")),

            // Purge old agent conversations — daily at 03:15 UTC.
            ("agent-conversation-retention", () => RecurringJob.AddOrUpdate<AgentConversationRetentionJob>(
                "agent-conversation-retention", job => job.ExecuteAsync(CancellationToken.None), "15 3 * * *")),

        };

        if (!string.IsNullOrWhiteSpace(mailerAudienceCron))
        {
            jobs = jobs.Append(("mailer-audience-sync", () => RecurringJob.AddOrUpdate<MailerAudienceSyncJob>(
                "mailer-audience-sync", job => job.ExecuteAsync(CancellationToken.None), mailerAudienceCron))).ToArray();
        }
        else
        {
            // Best-effort cleanup so a previously-registered job doesn't stick around
            // after operators disable the schedule by clearing the config value.
            try { RecurringJob.RemoveIfExists("mailer-audience-sync"); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove mailer-audience-sync recurring job entry");
            }
        }

        // Retired job entries — best-effort cleanup so old Hangfire records
        // referencing deleted job types don't try to instantiate them.
        foreach (var retired in new[] { "send-board-daily-digest", "send-admin-daily-digest" })
        {
            try { RecurringJob.RemoveIfExists(retired); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove retired recurring job '{JobId}'", retired);
            }
        }

        foreach (var (id, register) in jobs)
        {
            try
            {
                register();
            }
            catch (Exception ex)
            {
                // Don't let a stale distributed lock prevent the app from starting.
                // Existing job registrations in the DB will continue running.
                logger.LogWarning(ex, "Failed to register recurring job '{JobId}' — will retry on next restart", id);
            }
        }
    }
}
