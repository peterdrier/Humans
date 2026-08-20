using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Base.Hosting;

/// <summary>
/// Base for a section's observable-gauge refresh loop: owns the 60-second timer that
/// drives <see cref="RefreshAsync"/>, so each metrics gauge service (nobodies-collective/Humans#1091)
/// only has to implement the read side. Subclasses create their own
/// <c>Meter("Humans.Metrics")</c> and register gauges against a private snapshot updated by
/// <see cref="RefreshAsync"/> — multiple <c>Meter</c> instances may share a name; listeners
/// aggregate by name.
/// </summary>
public abstract class PolledGaugeService(IServiceScopeFactory scopeFactory, ILogger logger)
    : IHostedService, IDisposable
{
    /// <summary>Scope factory for subclasses whose gauge reads need scoped section services.</summary>
    protected IServiceScopeFactory ScopeFactory { get; } = scopeFactory;

    private Timer? _refreshTimer;

    /// <summary>
    /// Arms the gauge-refresh timer. Deliberately done here (StartAsync) rather than in the
    /// constructor: the host runs every <see cref="IHostedLifecycleService"/>.StartingAsync —
    /// including DatabaseMigrationHostedService, which applies pending migrations — to completion
    /// before any StartAsync. Arming in the constructor (via an eager resolve before app.Run)
    /// let the first refresh race schema migrations and query not-yet-migrated tables.
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
