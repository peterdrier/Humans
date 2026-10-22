using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Base.Hosting;

/// <summary>
/// Base for a section's observable-gauge refresh loop: owns the 60-second timer driving <see cref="RefreshAsync"/>.
/// Subclasses create their own <c>Meter("Humans.Metrics")</c> (instances may share a name; listeners aggregate by
/// name) and register gauges against a snapshot <see cref="RefreshAsync"/> updates.
/// </summary>
public abstract class PolledGaugeService(IServiceScopeFactory scopeFactory, ILogger logger)
    : IHostedService, IDisposable
{
    /// <summary>Scope factory for subclasses whose gauge reads need scoped section services.</summary>
    protected IServiceScopeFactory ScopeFactory { get; } = scopeFactory;

    private Timer? _refreshTimer;

    /// <summary>
    /// Arms the timer here, not in the constructor: the host runs every
    /// <see cref="IHostedLifecycleService"/>.StartingAsync, including the migration runner, before any StartAsync, so
    /// the first refresh never queries a not-yet-migrated table.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _refreshTimer = new Timer(
            callback: state => _ = RefreshSafeAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromSeconds(60));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <summary>Re-reads section state and updates the gauge snapshot.</summary>
    protected abstract Task RefreshAsync();

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh {Service} gauge snapshot", GetType().Name);
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}
