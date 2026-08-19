using System.Diagnostics.Metrics;
using Humans.Base.Hosting;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Teams.Services;

/// <summary>
/// Owns the Teams-section observable gauges split out of <c>HumansMetricsService</c>
/// (nobodies-collective/Humans#1091): teams by status and pending join requests.
/// </summary>
internal sealed class TeamsMetricsService : PolledGaugeService
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    private volatile GaugeSnapshot _snapshot = GaugeSnapshot.Empty;

    public TeamsMetricsService(IServiceScopeFactory scopeFactory, ILogger<TeamsMetricsService> logger)
        : base(scopeFactory, logger)
    {
        HumansMeter.CreateObservableGauge(
            "humans.teams",
            observeValues: ObserveTeams,
            description: "Teams by status");

        HumansMeter.CreateObservableGauge(
            "humans.team_join_requests_pending",
            observeValue: () => _snapshot.TeamJoinRequestsPending,
            description: "Pending team join requests");
    }

    private IEnumerable<Measurement<int>> ObserveTeams()
    {
        var s = _snapshot;
        yield return new Measurement<int>(s.TeamsActive, new KeyValuePair<string, object?>("status", "active"));
        yield return new Measurement<int>(s.TeamsInactive, new KeyValuePair<string, object?>("status", "inactive"));
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var teamService = scope.ServiceProvider.GetRequiredService<ITeamServiceRead>();

        var teams = await teamService.GetTeamsAsync(CancellationToken.None);
        var teamsActive = teams.Values.Count(t => t.IsActive);
        var teamsInactive = teams.Count - teamsActive;
        var teamJoinRequestsPending = teams.Values.Sum(t => t.PendingRequestCount);

        _snapshot = new GaugeSnapshot
        {
            TeamsActive = teamsActive,
            TeamsInactive = teamsInactive,
            TeamJoinRequestsPending = teamJoinRequestsPending
        };
    }

    private sealed record GaugeSnapshot
    {
        public static readonly GaugeSnapshot Empty = new();

        public int TeamsActive { get; init; }
        public int TeamsInactive { get; init; }
        public int TeamJoinRequestsPending { get; init; }
    }
}
