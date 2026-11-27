using System.Diagnostics.Metrics;
using Humans.Base.Hosting;
using Humans.Governance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Governance.Services;

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
    }

    private IEnumerable<Measurement<int>> ObserveApplicationsPending()
    {
        yield return new Measurement<int>(
            _snapshot.ApplicationsSubmitted, new KeyValuePair<string, object?>("status", "submitted"));
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var applicationDecisionService = scope.ServiceProvider.GetRequiredService<IGovernanceManagement>();

        var today = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant().InUtc().Date;
        var asociados = await scope.ServiceProvider.GetRequiredService<IGovernanceService>().GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, today);
        var applicationsSubmitted = await applicationDecisionService.GetPendingApplicationCountAsync();

        _snapshot = new GaugeSnapshot
        {
            Asociados = asociados.Count,
            ApplicationsSubmitted = applicationsSubmitted
        };
    }

    private sealed record GaugeSnapshot
    {
        public static readonly GaugeSnapshot Empty = new();

        public int Asociados { get; init; }
        public int ApplicationsSubmitted { get; init; }
    }
}
