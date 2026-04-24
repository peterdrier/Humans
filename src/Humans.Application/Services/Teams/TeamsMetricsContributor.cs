using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Teams;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Application.Services.Teams;

/// <summary>
/// Teams section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Gauge-only: <c>humans.teams</c> (by status) and
/// <c>humans.team_join_requests_pending</c>. No counters owned by this section.
/// </summary>
public sealed class TeamsMetricsContributor : IMetricsContributor
{
    private readonly IServiceScopeFactory _scopeFactory;

    private volatile int _teamsActive;
    private volatile int _teamsInactive;
    private volatile int _teamJoinRequestsPending;

    public TeamsMetricsContributor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Initialize(IHumansMetrics metrics)
    {
        metrics.CreateObservableGauge<int>(
            "humans.teams",
            observeValues: ObserveTeams,
            description: "Teams by status");

        metrics.CreateObservableGauge<int>(
            "humans.team_join_requests_pending",
            observeValue: () => _teamJoinRequestsPending,
            description: "Pending team join requests");

        metrics.RegisterGaugeRefresher(RefreshAsync);
    }

    private IEnumerable<Measurement<int>> ObserveTeams()
    {
        yield return new Measurement<int>(_teamsActive, new KeyValuePair<string, object?>("status", "active"));
        yield return new Measurement<int>(_teamsInactive, new KeyValuePair<string, object?>("status", "inactive"));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var teamService = scope.ServiceProvider.GetRequiredService<ITeamService>();

        var allTeams = await teamService.GetAllTeamsAsync(cancellationToken);
        var active = 0;
        var inactive = 0;
        foreach (var team in allTeams)
        {
            if (team.IsActive) active++;
            else inactive++;
        }
        _teamsActive = active;
        _teamsInactive = inactive;

        _teamJoinRequestsPending =
            await teamService.GetTotalPendingJoinRequestCountAsync(cancellationToken);
    }
}
