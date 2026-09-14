using System.Diagnostics.Metrics;
using Humans.Base.Hosting;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Governance.Services;

/// <summary>
/// Governance's observable gauges: asociado count, pending tier applications, and the two
/// consent-completeness gauges (pending_consents also needs the total user count, read
/// cross-section via <see cref="IUserServiceRead"/>).
/// </summary>
internal sealed class GovernanceMetricsService : PolledGaugeService
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    private volatile GaugeSnapshot _snapshot = GaugeSnapshot.Empty;

    public GovernanceMetricsService(IServiceScopeFactory scopeFactory, ILogger<GovernanceMetricsService> logger)
        : base(scopeFactory, logger)
    {
        HumansMeter.CreateObservableGauge(
            "humans.asociados",
            observeValue: () => _snapshot.Asociados,
            description: "Approved asociado members");

        HumansMeter.CreateObservableGauge(
            "humans.applications_pending",
            observeValues: ObserveApplicationsPending,
            description: "Pending applications by status");

        HumansMeter.CreateObservableGauge(
            "humans.pending_consents",
            observeValue: () => _snapshot.PendingConsents,
            description: "Users missing required consents");

        HumansMeter.CreateObservableGauge(
            "humans.consent_deadline_approaching",
            observeValue: () => _snapshot.ConsentDeadlineApproaching,
            description: "Users past grace period not yet suspended");
    }

    private IEnumerable<Measurement<int>> ObserveApplicationsPending()
    {
        yield return new Measurement<int>(
            _snapshot.ApplicationsSubmitted, new KeyValuePair<string, object?>("status", "submitted"));
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var membershipCalc = scope.ServiceProvider.GetRequiredService<IMembershipCalculatorRead>();
        var applicationDecisionService = scope.ServiceProvider.GetRequiredService<IApplicationServiceRead>();
        var userService = scope.ServiceProvider.GetRequiredService<IUserServiceRead>();

        var userInfos = await userService.GetAllUserInfosAsync().ConfigureAwait(false);
        var allUserIds = userInfos.Select(u => u.Id).ToList();

        var usersWithAllConsents = await membershipCalc.GetUsersWithAllRequiredConsentsAsync(allUserIds);
        var pendingConsents = allUserIds.Count - usersWithAllConsents.Count;

        var usersRequiringUpdate = await membershipCalc.GetUsersRequiringStatusUpdateAsync();
        var consentDeadlineApproaching = usersRequiringUpdate.Count;

        var applicationStats = await applicationDecisionService.GetAdminStatsAsync();
        var applicationsSubmitted = await applicationDecisionService.GetPendingApplicationCountAsync();

        _snapshot = new GaugeSnapshot
        {
            Asociados = applicationStats.Approved,
            ApplicationsSubmitted = applicationsSubmitted,
            PendingConsents = pendingConsents,
            ConsentDeadlineApproaching = consentDeadlineApproaching
        };
    }

    private sealed record GaugeSnapshot
    {
        public static readonly GaugeSnapshot Empty = new();

        public int Asociados { get; init; }
        public int ApplicationsSubmitted { get; init; }
        public int PendingConsents { get; init; }
        public int ConsentDeadlineApproaching { get; init; }
    }
}
