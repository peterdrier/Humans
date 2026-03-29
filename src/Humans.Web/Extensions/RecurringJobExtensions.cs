using Hangfire;
using Humans.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;

namespace Humans.Web.Extensions;

public static class RecurringJobExtensions
{
    public static void UseHumansRecurringJobs(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RecurringJobExtensions));

        var ticketSyncInterval = app.Configuration.GetValue("TicketVendor:SyncIntervalMinutes", 15);

        var jobs = new (string Id, Action Register)[]
        {
            // Google sync jobs — controlled by SyncServiceSettings (Admin/SyncSettings).
            // Set service mode to "None" to disable without redeploying.
            ("system-team-sync", () => RecurringJob.AddOrUpdate<SystemTeamSyncJob>(
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

            // Send Board digest of new approvals — every other day (odd days of month) at 02:00 UTC.
            ("send-board-daily-digest", () => RecurringJob.AddOrUpdate<SendBoardDailyDigestJob>(
                "send-board-daily-digest", job => job.ExecuteAsync(CancellationToken.None), "0 2 1-31/2 * *")),

            // Send Admin daily digest of system health and pending actions at 02:30 UTC.
            ("send-admin-daily-digest", () => RecurringJob.AddOrUpdate<SendAdminDailyDigestJob>(
                "send-admin-daily-digest", job => job.ExecuteAsync(CancellationToken.None), "30 2 * * *")),

            ("process-email-outbox", () => RecurringJob.AddOrUpdate<ProcessEmailOutboxJob>(
                "process-email-outbox", job => job.ExecuteAsync(CancellationToken.None), "*/1 * * * *")),

            ("cleanup-email-outbox", () => RecurringJob.AddOrUpdate<CleanupEmailOutboxJob>(
                "cleanup-email-outbox", job => job.ExecuteAsync(CancellationToken.None), "0 3 * * 0")),

            // Sync ticket data from vendor at configured interval (default 15 min).
            ("ticket-vendor-sync", () => RecurringJob.AddOrUpdate<TicketSyncJob>(
                "ticket-vendor-sync", job => job.ExecuteAsync(CancellationToken.None), $"*/{ticketSyncInterval} * * * *")),
        };

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
