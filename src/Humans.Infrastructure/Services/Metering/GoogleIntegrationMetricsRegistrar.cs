using Humans.Application.Interfaces.Metering;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Metering;

/// <summary>
/// Registers the Google Integration section's
/// <c>humans.google_sync_outbox_pending</c> gauge with <see cref="IMeters"/>
/// at app startup. A 60-second tick refreshes the cached count via
/// <see cref="IGoogleSyncOutboxRepository"/>; the OTel observable callback
/// reads the cached field synchronously.
/// </summary>
public sealed class GoogleIntegrationMetricsRegistrar : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IMeters _meters;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GoogleIntegrationMetricsRegistrar> _logger;
    private Timer? _refreshTimer;

    private volatile int _pendingOutboxEvents;

    public GoogleIntegrationMetricsRegistrar(
        IMeters meters,
        IServiceScopeFactory scopeFactory,
        ILogger<GoogleIntegrationMetricsRegistrar> logger)
    {
        _meters = meters;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meters.RegisterObservableGauge(
            "humans.google_sync_outbox_pending",
            new MeterMetadata("Unprocessed Google sync outbox events", "{events}"),
            () => _pendingOutboxEvents);

        _refreshTimer = new Timer(
            callback: _ => _ = RefreshAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: RefreshInterval);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_refreshTimer is not null)
        {
            await _refreshTimer.DisposeAsync();
            _refreshTimer = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_refreshTimer is not null)
        {
            await _refreshTimer.DisposeAsync();
            _refreshTimer = null;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var outboxRepository = scope.ServiceProvider
                .GetRequiredService<IGoogleSyncOutboxRepository>();

            _pendingOutboxEvents = await outboxRepository.CountPendingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Google Integration section metrics");
        }
    }
}
