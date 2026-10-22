using System.Diagnostics.Metrics;
using Humans.Base.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.GoogleIntegration.Services;

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
        var teamResourceService = scope.ServiceProvider.GetRequiredService<ITeamResourceManagement>();
        var googleSyncService = scope.ServiceProvider.GetRequiredService<IGoogleSyncManagement>();

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
