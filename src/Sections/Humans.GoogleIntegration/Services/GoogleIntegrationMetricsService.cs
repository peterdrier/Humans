using System.Diagnostics.Metrics;
using Humans.Base.Hosting;
using Humans.GoogleIntegration.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Owns the GoogleIntegration-section observable gauges split out of
/// <c>HumansMetricsService</c> (nobodies-collective/Humans#1091): total Google resources
/// (<see cref="ITeamResourceService"/> — the resource-count read moved with the section that
/// owns <c>google_resources</c>, not Teams) and the unprocessed sync outbox.
/// </summary>
internal sealed class GoogleIntegrationMetricsService : PolledGaugeService
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    private volatile GaugeSnapshot _snapshot = GaugeSnapshot.Empty;

    public GoogleIntegrationMetricsService(IServiceScopeFactory scopeFactory, ILogger<GoogleIntegrationMetricsService> logger)
        : base(scopeFactory, logger)
    {
        HumansMeter.CreateObservableGauge(
            "humans.google_resources",
            observeValue: () => _snapshot.GoogleResources,
            description: "Total Google resources");

        HumansMeter.CreateObservableGauge(
            "humans.google_sync_outbox_pending",
            observeValue: () => _snapshot.PendingOutboxEvents,
            description: "Unprocessed Google sync outbox events");
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var teamResourceService = scope.ServiceProvider.GetRequiredService<ITeamResourceService>();
        var googleSyncService = scope.ServiceProvider.GetRequiredService<IGoogleSyncServiceRead>();

        var googleResources = await teamResourceService.GetResourceCountAsync();
        var pendingOutboxEvents = await googleSyncService.GetPendingSyncEventCountAsync();

        _snapshot = new GaugeSnapshot
        {
            GoogleResources = googleResources,
            PendingOutboxEvents = pendingOutboxEvents
        };
    }

    private sealed record GaugeSnapshot
    {
        public static readonly GaugeSnapshot Empty = new();

        public int GoogleResources { get; init; }
        public int PendingOutboxEvents { get; init; }
    }
}
