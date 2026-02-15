using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Singleton service that owns the "Humans.Metrics" meter and all application-level
/// counters and observable gauges. Gauge values are refreshed every 60 seconds from
/// the database via a background timer.
/// </summary>
public sealed class HumansMetricsService : IDisposable
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    // Counters
    private readonly Counter<long> _emailsSent;
    private readonly Counter<long> _consentsGiven;
    private readonly Counter<long> _membersSuspended;
    private readonly Counter<long> _volunteersApproved;
    private readonly Counter<long> _syncOperations;
    private readonly Counter<long> _applicationsProcessed;
    private readonly Counter<long> _jobRuns;

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

        // Counters
        _emailsSent = HumansMeter.CreateCounter<long>(
            "humans.emails_sent_total",
            description: "Total emails sent");

        _consentsGiven = HumansMeter.CreateCounter<long>(
            "humans.consents_given_total",
            description: "Total consent records created");

        _membersSuspended = HumansMeter.CreateCounter<long>(
            "humans.members_suspended_total",
            description: "Total member suspensions");

        _volunteersApproved = HumansMeter.CreateCounter<long>(
            "humans.volunteers_approved_total",
            description: "Total volunteers approved");

        _syncOperations = HumansMeter.CreateCounter<long>(
            "humans.sync_operations_total",
            description: "Total Google sync operations");

        _applicationsProcessed = HumansMeter.CreateCounter<long>(
            "humans.applications_processed_total",
            description: "Total asociado applications processed");

        _jobRuns = HumansMeter.CreateCounter<long>(
            "humans.job_runs_total",
            description: "Total background job runs");

        // Observable Gauges
        HumansMeter.CreateObservableGauge(
            "humans.humans_total",
            observeValues: ObserveHumansTotal,
            description: "Total humans by status");

        HumansMeter.CreateObservableGauge(
            "humans.pending_volunteers",
            observeValue: () => _snapshot.PendingVolunteers,
            description: "Volunteers awaiting board approval");

        HumansMeter.CreateObservableGauge(
            "humans.pending_consents",
            observeValue: () => _snapshot.PendingConsents,
            description: "Users missing required consents");

        HumansMeter.CreateObservableGauge(
            "humans.consent_deadline_approaching",
            observeValue: () => _snapshot.ConsentDeadlineApproaching,
            description: "Users past grace period not yet suspended");

        HumansMeter.CreateObservableGauge(
            "humans.pending_deletions",
            observeValue: () => _snapshot.PendingDeletions,
            description: "Accounts scheduled for deletion");

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

        // Timer: fire immediately, then every 60 seconds
        _refreshTimer = new Timer(
            callback: _ => _ = RefreshSnapshotAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromSeconds(60));
    }

    // --- Counter record methods ---

    public void RecordEmailSent(string template)
        => _emailsSent.Add(1, new KeyValuePair<string, object?>("template", template));

    public void RecordConsentGiven()
        => _consentsGiven.Add(1);

    public void RecordMemberSuspended(string source)
        => _membersSuspended.Add(1, new KeyValuePair<string, object?>("source", source));

    public void RecordVolunteerApproved()
        => _volunteersApproved.Add(1);

    public void RecordSyncOperation(string result)
        => _syncOperations.Add(1, new KeyValuePair<string, object?>("result", result));

    public void RecordApplicationProcessed(string action)
        => _applicationsProcessed.Add(1, new KeyValuePair<string, object?>("action", action));

    public void RecordJobRun(string job, string result)
        => _jobRuns.Add(1,
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("result", result));

    // --- Observable gauge callbacks ---

    private IEnumerable<Measurement<int>> ObserveHumansTotal()
    {
        var s = _snapshot;
        yield return new Measurement<int>(s.ActiveCount, new KeyValuePair<string, object?>("status", "active"));
        yield return new Measurement<int>(s.SuspendedCount, new KeyValuePair<string, object?>("status", "suspended"));
        yield return new Measurement<int>(s.PendingCount, new KeyValuePair<string, object?>("status", "pending"));
        yield return new Measurement<int>(s.InactiveCount, new KeyValuePair<string, object?>("status", "inactive"));
    }

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
        yield return new Measurement<int>(s.ApplicationsUnderReview, new KeyValuePair<string, object?>("status", "under_review"));
    }

    // --- Snapshot refresh ---

    private async Task RefreshSnapshotAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
            var membershipCalc = scope.ServiceProvider.GetRequiredService<IMembershipCalculator>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var now = clock.GetCurrentInstant();

            // humans_total by status
            var allUserIds = await db.Users.Select(u => u.Id).ToListAsync();
            var profileData = await db.Profiles
                .Select(p => new { p.UserId, p.IsApproved, p.IsSuspended })
                .ToListAsync();

            var profileLookup = profileData.ToDictionary(p => p.UserId);

            int activeCount = 0, suspendedCount = 0, pendingCount = 0, inactiveCount = 0;
            foreach (var userId in allUserIds)
            {
                if (profileLookup.TryGetValue(userId, out var p))
                {
                    if (p.IsSuspended) suspendedCount++;
                    else if (!p.IsApproved) pendingCount++;
                    else activeCount++;
                }
                else
                {
                    inactiveCount++;
                }
            }

            // pending_consents
            var usersWithAllConsents = await membershipCalc.GetUsersWithAllRequiredConsentsAsync(allUserIds);
            var pendingConsents = allUserIds.Count - usersWithAllConsents.Count;

            // consent_deadline_approaching
            var usersRequiringUpdate = await membershipCalc.GetUsersRequiringStatusUpdateAsync();
            var consentDeadlineApproaching = usersRequiringUpdate.Count;

            // pending_deletions
            var pendingDeletions = await db.Users.CountAsync(u => u.DeletionScheduledFor != null);

            // asociados
            var asociados = await db.Applications.CountAsync(a => a.Status == ApplicationStatus.Approved);

            // role_assignments_active
            var roleAssignments = await db.RoleAssignments
                .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
                .GroupBy(ra => ra.RoleName)
                .Select(g => new { Role = g.Key, Count = g.Count() })
                .ToListAsync();

            // teams
            var teamsActive = await db.Teams.CountAsync(t => t.IsActive);
            var teamsInactive = await db.Teams.CountAsync(t => !t.IsActive);

            // team_join_requests_pending
            var teamJoinRequestsPending = await db.TeamJoinRequests
                .CountAsync(r => r.Status == TeamJoinRequestStatus.Pending);

            // google_resources
            var googleResources = await db.GoogleResources.CountAsync();

            // legal_documents_active
            var legalDocumentsActive = await db.LegalDocuments
                .CountAsync(d => d.IsActive && d.IsRequired);

            // applications_pending
            var applicationsSubmitted = await db.Applications
                .CountAsync(a => a.Status == ApplicationStatus.Submitted);
            var applicationsUnderReview = await db.Applications
                .CountAsync(a => a.Status == ApplicationStatus.UnderReview);

            _snapshot = new GaugeSnapshot
            {
                ActiveCount = activeCount,
                SuspendedCount = suspendedCount,
                PendingCount = pendingCount,
                InactiveCount = inactiveCount,
                PendingVolunteers = pendingCount,
                PendingConsents = pendingConsents,
                ConsentDeadlineApproaching = consentDeadlineApproaching,
                PendingDeletions = pendingDeletions,
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
                ApplicationsUnderReview = applicationsUnderReview
            };

            _logger.LogDebug("Metrics snapshot refreshed: {Active} active, {Suspended} suspended, {Pending} pending",
                activeCount, suspendedCount, pendingCount);
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

        // humans_total
        public int ActiveCount { get; init; }
        public int SuspendedCount { get; init; }
        public int PendingCount { get; init; }
        public int InactiveCount { get; init; }

        // Simple gauges
        public int PendingVolunteers { get; init; }
        public int PendingConsents { get; init; }
        public int ConsentDeadlineApproaching { get; init; }
        public int PendingDeletions { get; init; }
        public int Asociados { get; init; }

        // Role assignments grouped by role
        public IReadOnlyList<(string Role, int Count)> RoleAssignmentsByRole { get; init; } = [];

        // Teams
        public int TeamsActive { get; init; }
        public int TeamsInactive { get; init; }

        // Other
        public int TeamJoinRequestsPending { get; init; }
        public int GoogleResources { get; init; }
        public int LegalDocumentsActive { get; init; }
        public int ApplicationsSubmitted { get; init; }
        public int ApplicationsUnderReview { get; init; }
    }
}
