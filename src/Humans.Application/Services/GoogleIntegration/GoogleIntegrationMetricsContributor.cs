using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Google Integration section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Owns the <c>humans.sync_operations_total</c> counter plus two gauges:
/// <c>humans.google_sync_outbox_pending</c> and <c>humans.google_resources</c>.
/// </summary>
public sealed class GoogleIntegrationMetricsContributor : IGoogleSyncMetrics, IMetricsContributor
{
    private readonly IServiceScopeFactory _scopeFactory;

    private Counter<long> _syncOperations = null!;
    private volatile int _pendingOutboxEvents;
    private volatile int _googleResources;

    public GoogleIntegrationMetricsContributor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Initialize(IHumansMetrics metrics)
    {
        _syncOperations = metrics.CreateCounter<long>(
            "humans.sync_operations_total",
            description: "Total Google sync operations");

        metrics.CreateObservableGauge<int>(
            "humans.google_sync_outbox_pending",
            observeValue: () => _pendingOutboxEvents,
            description: "Unprocessed Google sync outbox events");

        metrics.CreateObservableGauge<int>(
            "humans.google_resources",
            observeValue: () => _googleResources,
            description: "Total Google resources");

        metrics.RegisterGaugeRefresher(RefreshAsync);
    }

    public void RecordSyncOperation(string result) =>
        _syncOperations.Add(1, new KeyValuePair<string, object?>("result", result));

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var outboxRepo = scope.ServiceProvider.GetRequiredService<IGoogleSyncOutboxRepository>();
        _pendingOutboxEvents = await outboxRepo.CountPendingAsync(cancellationToken);

        var teamResourceService = scope.ServiceProvider.GetRequiredService<ITeamResourceService>();
        _googleResources = await teamResourceService.GetResourceCountAsync(cancellationToken);
    }
}
