using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Application.Interfaces.Teams;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Singleton service that owns the "Humans.Metrics" meter and the counters /
/// observable gauges that have not yet been migrated to <c>IMeters</c>.
/// Migrated gauges (Profile/Users): humans_total, pending_volunteers,
/// pending_consents, consent_deadline_approaching, pending_deletions.
/// Migrated counters (Profile/Users): consents_given_total,
/// volunteers_approved_total, members_suspended_total. The remaining gauges +
/// counters here are scheduled for migration in subsequent commits of issue
/// nobodies-collective/Humans#580.
/// </summary>
public sealed class HumansMetricsService : IHumansMetrics, IDisposable
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    private readonly Counter<long> _emailsSent;
    private readonly Counter<long> _syncOperations;
    private readonly Counter<long> _applicationsProcessed;
    private readonly Counter<long> _jobRuns;
    private readonly Counter<long> _emailsQueued;
    private readonly Counter<long> _emailsFailed;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HumansMetricsService> _logger;
    private readonly Timer _refreshTimer;

    private volatile GaugeSnapshot _snapshot = GaugeSnapshot.Empty;

    public HumansMetricsService(
        IServiceScopeFactory scopeFactory,
        ILogger<HumansMetricsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _emailsSent = HumansMeter.CreateCounter<long>(
            "humans.emails_sent_total",
            description: "Total emails sent");

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
            "humans.asociados",
            observeValue: () => _snapshot.Asociados,
            description: "Approved asociado members");

        HumansMeter.CreateObservableGauge(
            "humans.role_assignments_active",
            observeValues: ObserveRoleAssignments,
            description: "Active role assignments by role");

        HumansMeter.CreateObservableGauge(
            "humans.teams",
            observeValues: ObserveTeams,
            description: "Teams by status");

        HumansMeter.CreateObservableGauge(
            "humans.team_join_requests_pending",
            observeValue: () => _snapshot.TeamJoinRequestsPending,
            description: "Pending team join requests");

        HumansMeter.CreateObservableGauge(
            "humans.google_resources",
            observeValue: () => _snapshot.GoogleResources,
            description: "Total Google resources");

        HumansMeter.CreateObservableGauge(
            "humans.legal_documents_active",
            observeValue: () => _snapshot.LegalDocumentsActive,
            description: "Active required legal documents");

        HumansMeter.CreateObservableGauge(
            "humans.applications_pending",
            observeValues: ObserveApplicationsPending,
            description: "Pending applications by status");

        HumansMeter.CreateObservableGauge(
            "humans.google_sync_outbox_pending",
            observeValue: () => _snapshot.PendingOutboxEvents,
            description: "Unprocessed Google sync outbox events");

        _refreshTimer = new Timer(
            callback: _ => _ = RefreshSnapshotAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromSeconds(60));
    }

    public void RecordEmailSent(string template)
        => _emailsSent.Add(1, new KeyValuePair<string, object?>("template", template));

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

    private IEnumerable<Measurement<int>> ObserveRoleAssignments()
    {
        foreach (var (role, count) in _snapshot.RoleAssignmentsByRole)
        {
            yield return new Measurement<int>(count, new KeyValuePair<string, object?>("role", role));
        }
    }

    private IEnumerable<Measurement<int>> ObserveTeams()
    {
        var s = _snapshot;
        yield return new Measurement<int>(s.TeamsActive, new KeyValuePair<string, object?>("status", "active"));
        yield return new Measurement<int>(s.TeamsInactive, new KeyValuePair<string, object?>("status", "inactive"));
    }

    private IEnumerable<Measurement<int>> ObserveApplicationsPending()
    {
        var s = _snapshot;
        yield return new Measurement<int>(s.ApplicationsSubmitted, new KeyValuePair<string, object?>("status", "submitted"));
    }

    private async Task RefreshSnapshotAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var now = clock.GetCurrentInstant();

            var asociados = await db.Applications.CountAsync(a => a.Status == ApplicationStatus.Approved);

            var roleAssignments = await db.RoleAssignments
                .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
                .GroupBy(ra => ra.RoleName)
                .Select(g => new { Role = g.Key, Count = g.Count() })
                .ToListAsync();

            var teamsActive = await db.Teams.CountAsync(t => t.IsActive);
            var teamsInactive = await db.Teams.CountAsync(t => !t.IsActive);

            var teamJoinRequestsPending = await db.TeamJoinRequests
                .CountAsync(r => r.Status == TeamJoinRequestStatus.Pending);

            var teamResourceService = scope.ServiceProvider.GetRequiredService<ITeamResourceService>();
            var googleResources = await teamResourceService.GetResourceCountAsync();

            var legalDocumentsActive = await db.LegalDocuments
                .CountAsync(d => d.IsActive && d.IsRequired);

            var applicationsSubmitted = await db.Applications
                .CountAsync(a => a.Status == ApplicationStatus.Submitted);

            var outboxRepo = scope.ServiceProvider.GetRequiredService<IGoogleSyncOutboxRepository>();
            var pendingOutboxEvents = await outboxRepo.CountPendingAsync();

            _snapshot = new GaugeSnapshot
            {
                Asociados = asociados,
                RoleAssignmentsByRole = roleAssignments
                    .Select(r => (r.Role, r.Count))
                    .ToList(),
                TeamsActive = teamsActive,
                TeamsInactive = teamsInactive,
                TeamJoinRequestsPending = teamJoinRequestsPending,
                GoogleResources = googleResources,
                LegalDocumentsActive = legalDocumentsActive,
                ApplicationsSubmitted = applicationsSubmitted,
                PendingOutboxEvents = pendingOutboxEvents,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh metrics snapshot");
        }
    }

    public void Dispose()
    {
        _refreshTimer.Dispose();
    }

    private sealed record GaugeSnapshot
    {
        public static readonly GaugeSnapshot Empty = new();

        public int Asociados { get; init; }
        public IReadOnlyList<(string Role, int Count)> RoleAssignmentsByRole { get; init; } = [];
        public int TeamsActive { get; init; }
        public int TeamsInactive { get; init; }
        public int TeamJoinRequestsPending { get; init; }
        public int GoogleResources { get; init; }
        public int LegalDocumentsActive { get; init; }
        public int ApplicationsSubmitted { get; init; }
        public int PendingOutboxEvents { get; init; }
    }
}
