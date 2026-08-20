using System.Diagnostics.Metrics;
using NodaTime;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Web.Services;

/// <summary>
/// Singleton service that owns the "Humans.Metrics" meter and all application-level
/// counters, plus the gauges that read only Users/Auth/Base state. The section-owned gauges
/// (Governance, Auth, Teams, GoogleIntegration, Consent) split out into their own
/// <c>PolledGaugeService</c> subclasses at nobodies-collective/Humans#1091 — see
/// <c>GovernanceMetricsService</c> and siblings. Gauge values here are refreshed every 60
/// seconds from the database via <see cref="PolledGaugeService"/>'s background timer.
/// </summary>
public sealed class HumansMetricsService : PolledGaugeService, IHumansMetrics
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    // Counters
    private readonly Counter<long> _emailsSent;
    private readonly Counter<long> _consentsGiven;
    private readonly Counter<long> _membersSuspended;
    private readonly Counter<long> _syncOperations;
    private readonly Counter<long> _applicationsProcessed;
    private readonly Counter<long> _jobRuns;
    private readonly Counter<long> _emailsQueued;
    private readonly Counter<long> _emailsFailed;

    private readonly IUserActivityTracker _activityTracker;

    private volatile GaugeSnapshot _snapshot = GaugeSnapshot.Empty;

    public HumansMetricsService(
        IServiceScopeFactory scopeFactory,
        ILogger<HumansMetricsService> logger,
        IUserActivityTracker activityTracker)
        : base(scopeFactory, logger)
    {
        _activityTracker = activityTracker;

        _emailsSent = HumansMeter.CreateCounter<long>(
            "humans.emails_sent_total",
            description: "Total emails sent");

        _consentsGiven = HumansMeter.CreateCounter<long>(
            "humans.consents_given_total",
            description: "Total consent records created");

        _membersSuspended = HumansMeter.CreateCounter<long>(
            "humans.members_suspended_total",
            description: "Total member suspensions");

        _syncOperations = HumansMeter.CreateCounter<long>(
            "humans.sync_operations_total",
            description: "Total Google sync operations");

        _applicationsProcessed = HumansMeter.CreateCounter<long>(
            "humans.applications_processed_total",
            description: "Total asociado applications processed");

        _jobRuns = HumansMeter.CreateCounter<long>(
            "humans.job_runs_total",
            description: "Total background job runs");

        _emailsQueued = HumansMeter.CreateCounter<long>(
            "humans.email_queued_total",
            description: "Total emails queued for sending");

        _emailsFailed = HumansMeter.CreateCounter<long>(
            "humans.email_failed_total",
            description: "Total email send failures");

        HumansMeter.CreateObservableGauge(
            "humans.humans_total",
            observeValues: ObserveHumansTotal,
            description: "Total humans by status");

        HumansMeter.CreateObservableGauge(
            "humans.pending_volunteers",
            observeValue: () => _snapshot.PendingVolunteers,
            description: "Volunteers awaiting board approval");

        HumansMeter.CreateObservableGauge(
            "humans.pending_deletions",
            observeValue: () => _snapshot.PendingDeletions,
            description: "Accounts scheduled for deletion");

        HumansMeter.CreateObservableGauge(
            "humans.active_users",
            observeValues: ObserveActiveUsers,
            description: "Distinct users with an authenticated request in the trailing window. In-memory only; resets on process restart.");

        // humans.email_outbox_pending lives on IMeters via ProcessEmailOutboxJob.
    }

    public void RecordEmailSent(string template)
        => _emailsSent.Add(1, new KeyValuePair<string, object?>("template", template));

    public void RecordConsentGiven()
        => _consentsGiven.Add(1);

    public void RecordMemberSuspended(string source)
        => _membersSuspended.Add(1, new KeyValuePair<string, object?>("source", source));

    public void RecordSyncOperation(string result)
        => _syncOperations.Add(1, new KeyValuePair<string, object?>("result", result));

    public void RecordApplicationProcessed(string action)
        => _applicationsProcessed.Add(1, new KeyValuePair<string, object?>("action", action));

    public void RecordJobRun(string job, string result)
        => _jobRuns.Add(1,
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("result", result));

    public void RecordEmailQueued(string template)
        => _emailsQueued.Add(1, new KeyValuePair<string, object?>("template", template));

    public void RecordEmailFailed(string template)
        => _emailsFailed.Add(1, new KeyValuePair<string, object?>("template", template));

    private IEnumerable<Measurement<int>> ObserveHumansTotal()
    {
        var s = _snapshot;
        yield return new Measurement<int>(s.ActiveCount, new KeyValuePair<string, object?>("status", "active"));
        yield return new Measurement<int>(s.SuspendedCount, new KeyValuePair<string, object?>("status", "suspended"));
        yield return new Measurement<int>(s.PendingCount, new KeyValuePair<string, object?>("status", "pending"));
        yield return new Measurement<int>(s.InactiveCount, new KeyValuePair<string, object?>("status", "inactive"));
    }

    private IEnumerable<Measurement<int>> ObserveActiveUsers()
    {
        yield return new Measurement<int>(
            _activityTracker.CountActiveWithin(Duration.FromMinutes(5)),
            new KeyValuePair<string, object?>("window", "5m"));
        yield return new Measurement<int>(
            _activityTracker.CountActiveWithin(Duration.FromHours(1)),
            new KeyValuePair<string, object?>("window", "1h"));
        yield return new Measurement<int>(
            _activityTracker.CountActiveWithin(Duration.FromHours(24)),
            new KeyValuePair<string, object?>("window", "24h"));
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserServiceRead>();

        // Read off cached UserInfo snapshot — avoids full profile scan per scrape.
        var userInfos = await userService.GetAllUserInfosAsync().ConfigureAwait(false);

        int activeCount = 0, suspendedCount = 0, pendingCount = 0, inactiveCount = 0;
        foreach (var userInfo in userInfos)
        {
            if (userInfo.Profile is null)
            {
                inactiveCount++;
            }
            else if (userInfo.IsSuspended)
            {
                suspendedCount++;
            }
            else if (!userInfo.IsApproved)
            {
                pendingCount++;
            }
            else
            {
                activeCount++;
            }
        }

        var pendingDeletions = userInfos.Count(u => u.DeletionScheduledFor != null);

        _snapshot = new GaugeSnapshot
        {
            ActiveCount = activeCount,
            SuspendedCount = suspendedCount,
            PendingCount = pendingCount,
            InactiveCount = inactiveCount,
            PendingVolunteers = pendingCount,
            PendingDeletions = pendingDeletions
        };
    }

    private sealed record GaugeSnapshot
    {
        public static readonly GaugeSnapshot Empty = new();

        public int ActiveCount { get; init; }
        public int SuspendedCount { get; init; }
        public int PendingCount { get; init; }
        public int InactiveCount { get; init; }

        public int PendingVolunteers { get; init; }
        public int PendingDeletions { get; init; }
    }
}
