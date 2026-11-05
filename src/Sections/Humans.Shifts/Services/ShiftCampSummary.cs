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
    IShiftManagementRepository repo,
    IServiceProvider serviceProvider) : IApplicationService
{
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();
    private ICampService CampService => serviceProvider.GetRequiredService<ICampService>();

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
        Rota? rota = null;

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
                rota = await repo.GetRotaAsync(rotaId.Value, RotaReadShape.Shifts, ct);
                if (rota is null
                    || rota.EventSettingsId != activeEvent.Id
                    || !teamSet.Contains(rota.TeamId))
                {
                    return null;
                }

                scope = ShiftSummaryScope.Rota;
                filterRotaId = rotaId;
            }
        }

        var totals = await repo.GetConfirmedUserShiftTotalsAsync(
            activeEvent.Id, filterTeamIds, filterRotaId, ct);

        var userIds = totals.Select(t => t.UserId).ToList();
        var userInfos = await UserService.GetUserInfosAsync(userIds, ct);

        var campByUser = new Dictionary<Guid, CampSeasonInfo?>();
        foreach (var userId in userIds)
            campByUser[userId] = (await CampService.GetCampUserInfoAsync(userId, ct)).Season;

        var humans = totals
            .Select(t =>
            {
                var camp = campByUser[t.UserId];
                var name = userInfos.TryGetValue(t.UserId, out var ui) ? ui.BurnerName : string.Empty;
                return new ShiftSummaryHumanRow(
                    t.UserId, name, camp?.CampId, camp?.Name, t.Hours, t.Count);
            })
            .ToList();

        var camps = await BuildCampPivotAsync(humans, ct);

        IReadOnlyList<ShiftSummaryTeamLink> teamLinks = scope == ShiftSummaryScope.Global
            ? await BuildDepartmentLinksAsync(activeEvent.Id, ct)
            : [];
        IReadOnlyList<ShiftSummaryRotaLink> rotaLinks = scope == ShiftSummaryScope.Team
            ? (await repo.GetRotasAsync(activeEvent.Id, filterTeamIds!, RotaReadShape.None, ct))
                .Select(r => new ShiftSummaryRotaLink(r.Id, r.Name)).ToList()
            : [];

        return new ShiftSummary(
            scope, team?.Name, team?.Slug, rota?.Name, rota?.Id,
            humans, camps, teamLinks, rotaLinks);
    }

    // Left-joins the active-camp roster so camps with nobody in scope show as zero rows; campless humans get a
    // CampId-null bucket.
    private async Task<IReadOnlyList<ShiftSummaryCampRow>> BuildCampPivotAsync(
        IReadOnlyList<ShiftSummaryHumanRow> humans, CancellationToken ct)
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

        var publicYear = (await CampService.GetSettingsAsync(ct)).PublicYear;
        var roster = await CampService.GetCampsForYearAsync(publicYear, ct);

        var rows = new List<ShiftSummaryCampRow>();
        var seen = new HashSet<Guid>();

        foreach (var camp in roster)
        {
            var season = camp.GetSeasonForYear(publicYear);
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
        Guid eventSettingsId, CancellationToken ct)
    {
        var teamIdsWithRotas = await repo.GetTeamIdsWithRotasInEventAsync(eventSettingsId, ct);
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
