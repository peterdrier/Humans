using Humans.Base.Constants;
using Humans.Google.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Shifts.Services;

/// <summary>
/// Each department's shift list for the active event, <c>{prefix}-{year}-shifts@</c>, holds everyone with a confirmed
/// shift on its rotas or its sub-teams' rotas. The prefix is the department's own group prefix, else its slug. Once
/// the event is no longer active nothing is claimed, so Google keeps each list frozen with its last members.
/// </summary>
internal sealed class ShiftGoogleGroupSource(
    IShiftRotaRows rotaRows,
    ITeamService teams,
    ISettingsService settings) : IGoogleGroupMembershipSource
{
    public async Task<Dictionary<string, Guid[]>> GetExpectedAsync(string? groupKey = null, CancellationToken ct = default)
    {
        if (settings.GetActiveEventSettings() is not { } activeEvent)
            return [];

        var requestedKey = string.IsNullOrWhiteSpace(groupKey) ? null : groupKey.Trim();
        var teamLookup = await teams.GetTeamsAsync(ct);
        return (await rotaRows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.EventSettingsId == activeEvent.Id)
            // Every department with a rota claims its list, even with nobody confirmed, so the last bail still removes.
            .GroupBy(r => ShiftStaffingAggregation.DepartmentIdOf(r.Rota.TeamId, teamLookup))
            .Where(d => teamLookup.ContainsKey(d.Key))
            .Select(d => (Key: ListEmail(teamLookup[d.Key], activeEvent.Year), UserIds: d
                .SelectMany(r => r.Shifts.SelectMany(s => s.Signups))
                .Where(s => s.Status == SignupStatus.Confirmed)
                .Select(s => s.UserId)))
            // Two departments whose names give one address claim nothing, so neither list gets the other's people.
            .GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1
                && (requestedKey is null || string.Equals(g.Key, requestedKey, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(g => g.Key, g => g.Single().UserIds.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    internal static string ListEmail(TeamInfo department, int year) =>
        $"{department.GoogleGroupPrefix ?? department.Slug}-{year}-shifts@{DomainConstants.GoogleGroupDomain}";
}
