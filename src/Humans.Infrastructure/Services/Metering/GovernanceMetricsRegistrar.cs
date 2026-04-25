using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Metering;

/// <summary>
/// Registers the Governance section's gauges (applications_pending, asociados)
/// with <see cref="IMeters"/> at app startup. A 60-second tick refreshes the
/// cached values via <see cref="IApplicationDecisionService"/>; OTel observable
/// callbacks read the cached fields synchronously.
/// </summary>
public sealed class GovernanceMetricsRegistrar : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IMeters _meters;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GovernanceMetricsRegistrar> _logger;
    private Timer? _refreshTimer;

    private volatile int _applicationsSubmitted;
    private volatile int _asociados;

    public GovernanceMetricsRegistrar(
        IMeters meters,
        IServiceScopeFactory scopeFactory,
        ILogger<GovernanceMetricsRegistrar> logger)
    {
        _meters = meters;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meters.RegisterObservableGauge(
            "humans.applications_pending",
            new MeterMetadata("Pending applications by status", "{applications}"),
            ObserveApplicationsPending);

        _meters.RegisterObservableGauge(
            "humans.asociados",
            new MeterMetadata("Approved asociado members", "{members}"),
            () => _asociados);

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

    private IEnumerable<Measurement<int>> ObserveApplicationsPending()
    {
        yield return new Measurement<int>(
            _applicationsSubmitted,
            new KeyValuePair<string, object?>("status", "submitted"));
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var applicationDecisionService = scope.ServiceProvider
                .GetRequiredService<IApplicationDecisionService>();

            _applicationsSubmitted = await applicationDecisionService.GetPendingApplicationCountAsync();
            _asociados = await applicationDecisionService.GetApprovedApplicationCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Governance section metrics");
        }
    }
}
