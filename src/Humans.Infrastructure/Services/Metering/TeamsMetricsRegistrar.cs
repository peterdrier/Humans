using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Metering;

/// <summary>
/// Registers the Teams section's gauges (humans.teams multi-measurement by
/// status, humans.team_join_requests_pending, humans.google_resources) with
/// <see cref="IMeters"/> at app startup. A 60-second tick refreshes the
/// cached values via <see cref="ITeamService"/> and
/// <see cref="ITeamResourceService"/>; OTel observable callbacks read the
/// cached fields synchronously.
/// </summary>
public sealed class TeamsMetricsRegistrar : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IMeters _meters;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TeamsMetricsRegistrar> _logger;
    private Timer? _refreshTimer;

    private volatile int _teamsActive;
    private volatile int _teamsInactive;
    private volatile int _teamJoinRequestsPending;
    private volatile int _googleResources;

    public TeamsMetricsRegistrar(
        IMeters meters,
        IServiceScopeFactory scopeFactory,
        ILogger<TeamsMetricsRegistrar> logger)
    {
        _meters = meters;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meters.RegisterObservableGauge(
            "humans.teams",
            new MeterMetadata("Teams by status", "{teams}"),
            ObserveTeams);

        _meters.RegisterObservableGauge(
            "humans.team_join_requests_pending",
            new MeterMetadata("Pending team join requests", "{requests}"),
            () => _teamJoinRequestsPending);

        _meters.RegisterObservableGauge(
            "humans.google_resources",
            new MeterMetadata("Total Google resources", "{resources}"),
            () => _googleResources);

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

    private IEnumerable<Measurement<int>> ObserveTeams()
    {
        yield return new Measurement<int>(
            _teamsActive,
            new KeyValuePair<string, object?>("status", "active"));
        yield return new Measurement<int>(
            _teamsInactive,
            new KeyValuePair<string, object?>("status", "inactive"));
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var teamService = scope.ServiceProvider.GetRequiredService<ITeamService>();
            var teamResourceService = scope.ServiceProvider
                .GetRequiredService<ITeamResourceService>();

            _teamsActive = await teamService.GetActiveTeamCountAsync();
            _teamsInactive = await teamService.GetInactiveTeamCountAsync();
            _teamJoinRequestsPending = await teamService.GetTotalPendingJoinRequestCountAsync();
            _googleResources = await teamResourceService.GetResourceCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Teams section metrics");
        }
    }
}
