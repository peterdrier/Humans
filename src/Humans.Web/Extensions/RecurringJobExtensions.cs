using System.Linq.Expressions;
using System.Reflection;
using Hangfire;
using Hangfire.Storage;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Agent.Jobs;
using Humans.Budget.Jobs;
using Humans.Consent.Jobs;
using Humans.Email.Jobs;
using Humans.Expenses.Jobs;
using Humans.Gate.Jobs;
using Humans.GoogleIntegration.Jobs;
using Humans.Governance.Jobs;
using Humans.Holded.Jobs;
using Humans.Issues.Jobs;
using Humans.Mailer.Jobs;
using Humans.Monitor.Jobs;
using Humans.Notifications.Jobs;
using Humans.Surveys.Jobs;
using Humans.Teams.Contracts;
using Humans.Tickets.Jobs;
using Humans.Users.Jobs;

namespace Humans.Web.Extensions;

public static class RecurringJobExtensions
{
    /// <summary>
    /// One entry in the roll-call: the Hangfire job id, the type Hangfire resolves from DI
    /// when the job fires, the cron it runs on (empty means "configured off"), and the call
    /// that writes the schedule.
    /// </summary>
    internal sealed record ScheduledJob(string Id, Type JobType, string Cron, Action Schedule);

    public static void UseHumansRecurringJobs(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RecurringJobExtensions));

        var registry = app.Services.GetRequiredService<ConfigurationRegistry>();
        var jobs = new List<ScheduledJob>(BuildRollCall(app.Configuration, registry));
        jobs.AddRange(ContributedJobs(app.Services));

        var allScheduled = true;

        foreach (var job in jobs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(job.Cron))
                {
                    // Opt-in job with no schedule configured. Drop any entry left from when
                    // it was switched on, so clearing the setting really stops the job.
                    RecurringJob.RemoveIfExists(job.Id);
                }
                else
                {
                    job.Schedule();
                }
            }
            catch (Exception ex)
            {
                // Don't let a stale distributed lock prevent the app from starting.
                allScheduled = false;
                logger.LogWarning(ex, "Failed to register recurring job '{JobId}' — will retry on next restart", job.Id);
            }
        }

        // Must run after the loop above: renaming a job id means writing the new entry
        // first and then sweeping the old one away.
        //
        // Only sweep when every job was written. If one failed, its old entry is still the
        // only working copy of that schedule, and we can't tell which stored id belongs to
        // the job that failed — removing it would stop the job until a later restart
        // happens to succeed. Skipping a sweep just leaves a dead entry around one more
        // boot, which is harmless.
        if (allScheduled)
        {
            RemoveJobsMissingFromRollCall(jobs, logger);
        }
        else
        {
            logger.LogWarning(
                "Skipped sweeping unknown recurring jobs because at least one schedule failed to register");
        }
    }

    /// <summary>
    /// The jobs sections contribute through <see cref="ISectionJobs"/>, in the same shape as
    /// the roll-call so scheduling and sweeping see one merged set.
    /// </summary>
    private static IEnumerable<ScheduledJob> ContributedJobs(IServiceProvider services) =>
        SectionDiscoveryExtensions.DiscoverImplementations<ISectionJobs>()
            .SelectMany(contributor => contributor.Jobs(services))
            .Select(ToScheduledJob);

    /// <summary>
    /// Turns a descriptor into the same <c>RecurringJob.AddOrUpdate&lt;TJob&gt;</c> call the
    /// roll-call makes. Resolving the job type is deferred into the schedule action so a
    /// malformed descriptor fails inside the per-job try/catch rather than while the list is
    /// being built, where it would stop the app booting.
    /// </summary>
    internal static ScheduledJob ToScheduledJob(RecurringJobDescriptor descriptor) =>
        new(descriptor.Id, descriptor.JobType, descriptor.Cron, () => ScheduleContributed(descriptor));

    /// <summary>
    /// The generic argument is the job type, which Shell only knows at runtime — hence the one
    /// reflection hop onto <see cref="ScheduleTyped{TJob}"/>, whose body is the ordinary
    /// compiled call.
    /// </summary>
    private static void ScheduleContributed(RecurringJobDescriptor descriptor)
    {
        var execute = descriptor.JobType.GetMethod(nameof(IRecurringJob.ExecuteAsync), [typeof(CancellationToken)])
            ?? throw new InvalidOperationException(
                $"Job '{descriptor.Id}' names {descriptor.JobType.FullName}, which has no ExecuteAsync(CancellationToken)");

        typeof(RecurringJobExtensions)
            .GetMethod(nameof(ScheduleTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(descriptor.JobType)
            .Invoke(null, [descriptor.Id, execute, descriptor.Cron]);
    }

    /// <summary>
    /// The roll-call's own scheduling call, reached generically. <typeparamref name="TJob"/>
    /// may be an interface — Teams schedules against <c>ISystemTeamSync</c> — so the entry
    /// point is found by signature rather than through <see cref="IRecurringJob"/>.
    /// </summary>
    private static void ScheduleTyped<TJob>(string id, MethodInfo execute, string cron) where TJob : class =>
        RecurringJob.AddOrUpdate(id, BuildCall<TJob>(execute), cron);

    /// <summary>
    /// <c>job =&gt; job.ExecuteAsync(CancellationToken.None)</c>, built rather than written.
    /// A job whose <c>ExecuteAsync</c> returns <c>Task&lt;T&gt;</c> — Teams' sync returns a
    /// report — still types as <c>Func&lt;TJob, Task&gt;</c>, exactly as the roll-call's
    /// hand-written lambda did.
    /// </summary>
    internal static Expression<Func<TJob, Task>> BuildCall<TJob>(MethodInfo execute) where TJob : class
    {
        var job = Expression.Parameter(typeof(TJob), "job");
        return Expression.Lambda<Func<TJob, Task>>(
            Expression.Call(job, execute, Expression.Constant(CancellationToken.None)),
            job);
    }

    /// <summary>
    /// Every recurring job the app knows how to run. Ids are section-first so the owner is
    /// obvious from the id alone. This list is the only source of truth — the startup path
    /// schedules from it and anything Hangfire has stored that is not in it gets removed.
    /// </summary>
    internal static IReadOnlyList<ScheduledJob> BuildRollCall(
        IConfiguration configuration,
        ConfigurationRegistry registry)
    {
        var ticketSyncInterval = configuration.GetSettingValue(
            registry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor", defaultValue: 15);
        // MailerLite:AudienceSyncCron is opt-in. When empty/unset the job keeps its place in
        // the roll-call but is not scheduled — admins still trigger syncs on demand via the
        // /Mailer/Admin "Push Now" button. Set to e.g. "0 6 * * *" to enable.
        var mailerAudienceCron = configuration.GetValue<string>("MailerLite:AudienceSyncCron")
            ?? string.Empty;

        var jobs = new List<ScheduledJob>();

        // Every job's Hangfire entry point is IRecurringJob.ExecuteAsync, so the id, the type
        // and the schedule are all the roll-call has to state.
        void Add<TJob>(string id, string cron) where TJob : IRecurringJob =>
            jobs.Add(new ScheduledJob(id, typeof(TJob), cron,
                () => RecurringJob.AddOrUpdate<TJob>(id, job => job.ExecuteAsync(CancellationToken.None), cron)));

        // Teams' system-team sweep is the one job scheduled against an interface rather than a
        // concrete type — ISystemTeamSync returns a report, so it can't implement IRecurringJob.
        // Hangfire resolves the implementation (Humans.Teams' SystemTeamSyncJob) from DI, and
        // the interface itself moved to Humans.Teams.Contracts at G5 lane 5c.
        void AddSystemTeamSync(string id, string cron) =>
            jobs.Add(new ScheduledJob(id, typeof(ISystemTeamSync), cron,
                () => RecurringJob.AddOrUpdate<ISystemTeamSync>(id, job => job.ExecuteAsync(CancellationToken.None), cron)));

        // Google sync jobs — controlled by SyncServiceSettings (Admin/SyncSettings).
        // Set service mode to "None" to disable without redeploying.
        AddSystemTeamSync("teams-system-sync", Cron.Hourly());

        Add<GoogleResourceReconciliationJob>("google-resource-reconciliation", "0 3 * * *");

        Add<ProcessAccountDeletionsJob>("users-account-deletions", Cron.Daily());

        Add<SyncLegalDocumentsJob>("consent-legal-document-sync", "0 4 * * *");

        Add<SuspendNonCompliantMembersJob>("users-suspend-non-compliant", "30 4 * * *");

        // Send re-consent reminders before the suspension job runs.
        // Runs daily at 04:00, 30 minutes before SuspendNonCompliantMembersJob.
        Add<SendReConsentReminderJob>("consent-reconsent-reminders", "0 4 * * *");

        Add<ProcessGoogleSyncOutboxJob>("google-sync-outbox-process", "*/10 * * * *");

        Add<DriveActivityMonitorJob>("monitor-drive-activity", Cron.Hourly());

        // Send term renewal reminders to Colaboradors/Asociados whose terms expire within 90 days.
        Add<TermRenewalReminderJob>("governance-term-renewal-reminder", "0 5 * * 1");

        Add<ProcessEmailOutboxJob>("email-outbox-process", "*/1 * * * *");

        Add<CleanupEmailOutboxJob>("email-outbox-cleanup", "0 3 * * 0");

        // Clean up resolved notifications older than 7 days — daily at 04:30 UTC.
        Add<CleanupNotificationsJob>("notifications-cleanup", "30 4 * * *");

        // Clean up issues 6 months after they entered a terminal state — daily at 05:00 UTC.
        Add<CleanupIssuesJob>("issues-cleanup", "0 5 * * *");

        // Sync ticket data from vendor at configured interval (default 15 min).
        Add<TicketSyncJob>("tickets-vendor-sync", $"*/{ticketSyncInterval} * * * *");

        // Materialize ticket sales actuals into budget line items daily at 04:30.
        Add<TicketingBudgetSyncJob>("budget-ticketing-sync", "30 4 * * *");

        // Push approved expense reports to Holded as purchase documents — every minute.
        Add<HoldedExpenseOutboxJob>("expenses-holded-outbox", "*/1 * * * *");

        // Nightly pull of Holded purchase docs → budget-category actuals + creditor daybook — daily at 03:00 UTC.
        Add<HoldedSyncJob>("holded-sync", "0 3 * * *");

        // Purge old agent conversations — daily at 03:15 UTC.
        Add<AgentConversationRetentionJob>("agent-conversation-retention", "15 3 * * *");

        // Send the one-time 7-day survey reminder to invitees who haven't completed — daily at 09:00 UTC.
        Add<SendSurveyReminderJob>("surveys-reminder", "0 9 * * *");

        // Purge gate scan events past the retention window (Gate:RetentionDays) — daily at 03:45 UTC.
        Add<GateRetentionJob>("gate-retention", "45 3 * * *");

        // Opt-in — see mailerAudienceCron above.
        Add<MailerAudienceSyncJob>("mailer-audience-sync", mailerAudienceCron);

        return jobs;
    }

    /// <summary>
    /// Deletes stored schedules for jobs the app no longer has. A job that was deleted or
    /// renamed leaves its old Hangfire row behind, and that row names a type that no longer
    /// exists — so it throws on every tick until someone notices. Best-effort: a failure
    /// here must not stop the app from starting.
    /// </summary>
    private static void RemoveJobsMissingFromRollCall(IReadOnlyList<ScheduledJob> jobs, ILogger logger)
    {
        try
        {
            // Opt-in jobs stay in the roll-call even when their schedule is switched off, so
            // turning one off never gets it swept away here.
            var known = jobs.Select(job => job.Id).ToHashSet(StringComparer.Ordinal);

            using var connection = JobStorage.Current.GetConnection();
            foreach (var stored in connection.GetRecurringJobs().Where(stored => !known.Contains(stored.Id)))
            {
                RecurringJob.RemoveIfExists(stored.Id);
                logger.LogInformation(
                    "Removed recurring job '{JobId}' — the app no longer has a job by that name", stored.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove recurring jobs the app no longer has");
        }
    }
}
