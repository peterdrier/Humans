using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Application.Services.Governance;

/// <summary>
/// Governance section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Owns the <c>humans.applications_processed_total</c> counter plus two gauges:
/// <c>humans.asociados</c> (approved-application count) and
/// <c>humans.applications_pending</c> (pending-by-status count).
/// </summary>
public sealed class GovernanceMetricsContributor : IApplicationMetrics, IMetricsContributor
{
    private readonly IServiceScopeFactory _scopeFactory;

    private Counter<long> _applicationsProcessed = null!;
    private volatile int _asociados;
    private volatile int _applicationsSubmitted;

    public GovernanceMetricsContributor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Initialize(IHumansMetrics metrics)
    {
        _applicationsProcessed = metrics.CreateCounter<long>(
            "humans.applications_processed_total",
            description: "Total asociado applications processed");

        metrics.CreateObservableGauge<int>(
            "humans.asociados",
            observeValue: () => _asociados,
            description: "Approved asociado members");

        metrics.CreateObservableGauge<int>(
            "humans.applications_pending",
            observeValues: ObserveApplicationsPending,
            description: "Pending applications by status");

        metrics.RegisterGaugeRefresher(RefreshAsync);
    }

    public void RecordApplicationProcessed(string action) =>
        _applicationsProcessed.Add(1, new KeyValuePair<string, object?>("action", action));

    private IEnumerable<Measurement<int>> ObserveApplicationsPending()
    {
        yield return new Measurement<int>(
            _applicationsSubmitted,
            new KeyValuePair<string, object?>("status", "submitted"));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var decisionService = scope.ServiceProvider.GetRequiredService<IApplicationDecisionService>();

        // GetAdminStatsAsync returns Approved + pending counts; reusing it avoids
        // duplicating count queries.
        var stats = await decisionService.GetAdminStatsAsync(cancellationToken);
        _asociados = stats.Approved;
        _applicationsSubmitted = await decisionService.GetPendingApplicationCountAsync(cancellationToken);
    }
}
