using Hangfire;
using Humans.Infrastructure.Jobs;

namespace Humans.Web.Extensions;

public static class RecurringJobExtensions
{
    public static void UseHumansRecurringJobs(this WebApplication _)
    {
        // Google sync jobs — controlled by SyncServiceSettings (Admin/SyncSettings).
        // Set service mode to "None" to disable without redeploying.
        RecurringJob.AddOrUpdate<SystemTeamSyncJob>(
            "system-team-sync",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Hourly);

        RecurringJob.AddOrUpdate<GoogleResourceReconciliationJob>(
            "google-resource-reconciliation",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 3 * * *"); // Daily at 03:00 UTC

        RecurringJob.AddOrUpdate<ProcessAccountDeletionsJob>(
            "process-account-deletions",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily);

        RecurringJob.AddOrUpdate<SyncLegalDocumentsJob>(
            "legal-document-sync",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 4 * * *");

        RecurringJob.AddOrUpdate<SuspendNonCompliantMembersJob>(
            "suspend-non-compliant-members",
            job => job.ExecuteAsync(CancellationToken.None),
            "30 4 * * *");

        // Send re-consent reminders before suspension job runs.
        // Runs daily at 04:00, 30 minutes before SuspendNonCompliantMembersJob.
        // Timing controlled by Email:ConsentReminderDaysBeforeSuspension and
        // Email:ConsentReminderCooldownDays in appsettings.
        RecurringJob.AddOrUpdate<SendReConsentReminderJob>(
            "send-reconsent-reminders",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 4 * * *");

        RecurringJob.AddOrUpdate<ProcessGoogleSyncOutboxJob>(
            "process-google-sync-outbox",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/10 * * * *");

        RecurringJob.AddOrUpdate<DriveActivityMonitorJob>(
            "drive-activity-monitor",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Hourly);

        // Send term renewal reminders to Colaboradors/Asociados whose terms expire within 90 days.
        // Runs weekly on Mondays at 05:00.
        RecurringJob.AddOrUpdate<TermRenewalReminderJob>(
            "term-renewal-reminder",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 5 * * 1");

        // Send Board daily digest of new approvals from the previous UTC day.
        // Runs daily at 02:00 UTC.
        RecurringJob.AddOrUpdate<SendBoardDailyDigestJob>(
            "send-board-daily-digest",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 2 * * *");

        RecurringJob.AddOrUpdate<ProcessEmailOutboxJob>(
            "process-email-outbox",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/1 * * * *"); // Every minute

        RecurringJob.AddOrUpdate<CleanupEmailOutboxJob>(
            "cleanup-email-outbox",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 3 * * 0"); // Sunday 03:00 UTC

        // Sync ticket data from vendor at configured interval (default 15 min).
        // Requires TICKET_VENDOR_API_KEY environment variable and TicketVendor:EventId in appsettings.
        var ticketSyncInterval = _.Configuration.GetValue("TicketVendor:SyncIntervalMinutes", 15);
        RecurringJob.AddOrUpdate<TicketSyncJob>(
            "ticket-vendor-sync",
            job => job.ExecuteAsync(CancellationToken.None),
            $"*/{ticketSyncInterval} * * * *");
    }
}
