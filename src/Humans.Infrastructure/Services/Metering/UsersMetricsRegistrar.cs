using Humans.Application.Interfaces.Metering;
using Humans.Application.Interfaces.Users;
using Humans.Application.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Metering;

/// <summary>
/// Registers the Users section's gauges with <see cref="IMeters"/> at app
/// startup and refreshes them every 60 seconds. OTel observable callbacks
/// read the cached field synchronously.
/// </summary>
public sealed class UsersMetricsRegistrar : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IMeters _meters;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UsersMetricsRegistrar> _logger;
    private Timer? _refreshTimer;

    private volatile int _pendingDeletions;

    public UsersMetricsRegistrar(
        IMeters meters,
        IServiceScopeFactory scopeFactory,
        ILogger<UsersMetricsRegistrar> logger)
    {
        _meters = meters;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meters.RegisterObservableGauge(
            "humans.pending_deletions",
            new MeterMetadata("Accounts scheduled for deletion", "{accounts}"),
            () => _pendingDeletions);

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
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            _pendingDeletions = await userService.GetScheduledDeletionCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Users section metrics");
        }
    }
}
