using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Shifts.Services;

internal sealed class ShiftCoordinatorActivity(
    IShiftManagementRepository repo,
    EventCalendarResolver calendarResolver,
    IServiceProvider serviceProvider) : IApplicationService
{
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();

    internal async Task<IReadOnlyList<CoordinatorActivityRow>> ComputeCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod, CancellationToken ct)
    {
        int? minDayOffset = null;
        int? maxDayOffset = null;

        if (period is not null)
        {
            var es = await calendarResolver.GetAsync(eventSettingsId, ct);
            if (es is null) return [];
            (minDayOffset, maxDayOffset) = BuildSubPeriodClassifier.GetDayOffsetBounds(period, subPeriod, es);
        }

        var pendingCounts = await repo.GetPendingSignupCountsByTeamAsync(
            eventSettingsId, minDayOffset, maxDayOffset, ct);

        if (pendingCounts.Count == 0)
            return [];

        var teamsById = await TeamService.GetTeamsAsync(ct);
        var teamMeta = ShiftStaffingAggregation.IncludeParents(teamsById, pendingCounts.Keys);

        var relevantTeamIds = new HashSet<Guid>(pendingCounts.Keys.Where(teamMeta.ContainsKey));
        foreach (var id in relevantTeamIds.ToList())
        {
            var current = teamMeta.GetValueOrDefault(id);
            while (current?.ParentTeamId is Guid parentId && teamMeta.ContainsKey(parentId))
            {
                if (!relevantTeamIds.Add(parentId))
                    break;
                current = teamMeta[parentId];
            }
        }

        var coordsRaw = relevantTeamIds
            .Where(teamsById.ContainsKey)
            .SelectMany(id => teamsById[id].Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .Select(m => new TeamCoordinatorRef(id, m.UserId)))
            .ToList();
        var coordinatorUserIds = coordsRaw.Select(c => c.UserId).Distinct().ToList();

        var userLookup = await UserService.GetUserInfosAsync(coordinatorUserIds, ct);

        var coordsByTeam = coordsRaw
            .GroupBy(c => c.TeamId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CoordinatorLogin>)g
                    .Select(c =>
                    {
                        userLookup.TryGetValue(c.UserId, out var user);
                        return new CoordinatorLogin(
                            c.UserId,
                            user?.LastLoginAt);
                    })
                    .ToList());

        CoordinatorActivityRow BuildRow(Guid teamId)
        {
            var t = teamMeta[teamId];
            var ownPending = pendingCounts.GetValueOrDefault(teamId, 0);
            var coords = coordsByTeam.GetValueOrDefault(teamId, []);

            var childIds = relevantTeamIds
                .Where(id => teamMeta[id].ParentTeamId == teamId);

            var subgroups = childIds
                .Select(BuildRow)
                .ToList();

            var aggregate = ownPending + subgroups.Sum(s => s.AggregatePendingCount);
            return new CoordinatorActivityRow(teamId, t.Name, coords, ownPending, aggregate, subgroups);
        }

        var rootIds = relevantTeamIds
            .Where(id =>
            {
                var t = teamMeta[id];
                return !t.ParentTeamId.HasValue || !relevantTeamIds.Contains(t.ParentTeamId.Value);
            });

        return rootIds
            .Select(BuildRow)
            .Where(r => r.AggregatePendingCount > 0)
            .ToList();
    }
}
