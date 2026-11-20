using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Shifts.Services;

internal sealed class ShiftCampSummary(
    IServiceProvider serviceProvider) : IApplicationService
{
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();
    private ICampService CampService => serviceProvider.GetRequiredService<ICampService>();
    private IShiftRotaRows RotaRows => serviceProvider.GetRequiredService<IShiftRotaRows>();

    public async Task<ShiftSummary?> BuildSummaryAsync(
        BurnSettingsInfo activeEvent,
        string? teamSlug = null,
        Guid? rotaId = null,
        CancellationToken ct = default)
    {
        // Null scope: the controller returns 404.
        ShiftSummaryScope scope;
        IReadOnlyCollection<Guid>? filterTeamIds = null;
        Guid? filterRotaId = null;
        TeamInfo? team = null;
        RotaInfo? rota = null;
        var eventRotas = (await RotaRows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.EventSettingsId == activeEvent.Id)
            .ToList();

        if (teamSlug is null)
        {
            scope = ShiftSummaryScope.Global;
        }
        else
        {
            team = await TeamService.GetTeamBySlugAsync(teamSlug, ct);
            if (team is null) return null;

            var teamSet = ShiftStaffingAggregation.ResolveDepartmentTeamIds(team.Id, await TeamService.GetTeamsAsync(ct));

            if (rotaId is null)
            {
                scope = ShiftSummaryScope.Team;
                filterTeamIds = teamSet;
            }
            else
            {
                rota = eventRotas.FirstOrDefault(r => r.Rota.Id == rotaId.Value)?.Rota;
                if (rota is null || !teamSet.Contains(rota.TeamId))
                {
                    return null;
                }

                scope = ShiftSummaryScope.Rota;
                filterRotaId = rotaId;
            }
        }

        // All-day rows store a 24h sentinel, so they count Shift.AllDayWindowHours instead.
        var totals = eventRotas
            .Where(r => filterRotaId is { } rid
                ? r.Rota.Id == rid
                : filterTeamIds is not { Count: > 0 } || filterTeamIds.Contains(r.Rota.TeamId))
            .SelectMany(r => r.Shifts.SelectMany(s => s.Signups
                .Where(su => su.Status == SignupStatus.Confirmed)
                .Select(su => (su.UserId, Hours: s.Shift.IsAllDay ? Shift.AllDayWindowHours : s.Shift.DurationHours))))
            .GroupBy(x => x.UserId)
            .Select(g => (UserId: g.Key, Hours: g.Sum(x => x.Hours), Count: g.Count()))
            .ToList();

        var userIds = totals.Select(t => t.UserId);
        var userInfos = await UserService.GetUserInfosAsync(userIds, ct);

        var campRoster = await CampService.GetCampsForEventAsync(activeEvent.Id, ct);

        var humans = totals
            .Select(t =>
            {
                var camp = CampUserInfo.Resolve(campRoster, activeEvent.Id, t.UserId).Season;
                var name = userInfos.TryGetValue(t.UserId, out var ui) ? ui.BurnerName : string.Empty;
                return new ShiftSummaryHumanRow(
                    t.UserId, name, camp?.CampId, camp?.Name, t.Hours, t.Count);
            })
            .ToList();

        var camps = BuildCampPivot(humans, campRoster, activeEvent.Id);

        IReadOnlyList<ShiftSummaryTeamLink> teamLinks = scope == ShiftSummaryScope.Global
            ? await BuildDepartmentLinksAsync([.. eventRotas.Select(r => r.Rota.TeamId).Distinct()], ct)
            : [];
        IReadOnlyList<ShiftSummaryRotaLink> rotaLinks = scope == ShiftSummaryScope.Team
            ? [.. eventRotas
                .Where(r => filterTeamIds!.Contains(r.Rota.TeamId))
                .Select(r => new ShiftSummaryRotaLink(r.Rota.Id, r.Rota.Name))]
            : [];

        return new ShiftSummary(
            scope, team?.Name, team?.Slug, rota?.Name, rota?.Id,
            humans, camps, teamLinks, rotaLinks);
    }

    // Left-joins the active-camp roster so camps with nobody in scope show as zero rows; campless humans get a
    // CampId-null bucket.
    private static IReadOnlyList<ShiftSummaryCampRow> BuildCampPivot(
        IReadOnlyList<ShiftSummaryHumanRow> humans, IReadOnlyList<CampInfo> roster, Guid eventSettingsId)
    {
        var byCamp = humans
            .Where(h => h.CampId is not null)
            .GroupBy(h => h.CampId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (
                    People: g.Count(),
                    Hours: g.Sum(h => h.Hours),
                    Count: g.Sum(h => h.Count),
                    Name: g.Select(h => h.CampName).FirstOrDefault(n => n is not null)));

        var rows = new List<ShiftSummaryCampRow>();
        var seen = new HashSet<Guid>();

        foreach (var camp in roster)
        {
            var season = camp.GetSeasonForEvent(eventSettingsId);
            if (season is not { Status: CampSeasonStatus.Active or CampSeasonStatus.Full })
                continue;

            seen.Add(camp.Id);
            rows.Add(byCamp.TryGetValue(camp.Id, out var hit)
                ? new ShiftSummaryCampRow(camp.Id, season.Name, hit.People, hit.Hours, hit.Count)
                : new ShiftSummaryCampRow(camp.Id, season.Name, 0, 0, 0));
        }

        // Camps the roster missed still get a row, so no hours silently vanish.
        foreach (var (campId, a) in byCamp)
            if (!seen.Contains(campId))
                rows.Add(new ShiftSummaryCampRow(campId, a.Name, a.People, a.Hours, a.Count));

        var campless = humans.Where(h => h.CampId is null).ToList();
        if (campless.Count > 0)
            rows.Add(new ShiftSummaryCampRow(
                null, null, campless.Count, campless.Sum(h => h.Hours), campless.Sum(h => h.Count)));

        return rows;
    }

    // A non-promoted sub-team folds into its parent; the inverse of the shared department expansion.
    private async Task<IReadOnlyList<ShiftSummaryTeamLink>> BuildDepartmentLinksAsync(
        IReadOnlyList<Guid> teamIdsWithRotas, CancellationToken ct)
    {
        if (teamIdsWithRotas.Count == 0) return [];

        var teams = await TeamService.GetTeamsAsync(ct);

        var departmentIds = new HashSet<Guid>();
        foreach (var teamId in teamIdsWithRotas)
        {
            if (!teams.TryGetValue(teamId, out var team)) continue;

            if (team.ParentTeamId is { } parentId
                && !team.IsPromotedToDirectory
                && teams.ContainsKey(parentId))
            {
                departmentIds.Add(parentId);
            }
            else
            {
                departmentIds.Add(teamId);
            }
        }

        return departmentIds
            .Select(id => teams[id])
            .Select(t => new ShiftSummaryTeamLink(t.Id, t.Name, t.Slug))
            .ToList();
    }
}
