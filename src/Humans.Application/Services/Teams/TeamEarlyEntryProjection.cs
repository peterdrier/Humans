using Humans.Application.Interfaces.EarlyEntry;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Teams;

/// <summary>
/// Pure projection from Teams' early-entry grants to EE grants. Per-grant date;
/// source label is team-derived as "{TeamName}: {ProjectName}" (mirroring Shifts'
/// "Shift: {team}" convention). Each grant's <see cref="TeamEarlyEntryGrant.Team"/>
/// nav MUST be loaded by the caller. Kept as a small pure, unit-tested helper
/// (Camps/Shifts now inline their equivalent projection in the provider).
/// </summary>
internal static class TeamEarlyEntryProjection
{
    internal static IReadOnlyList<EarlyEntryGrant> Project(IReadOnlyList<TeamEarlyEntryGrant> grants)
    {
        var result = new List<EarlyEntryGrant>(grants.Count);
        foreach (var g in grants)
            result.Add(new EarlyEntryGrant(g.UserId, g.EntryDate, $"{g.Team.Name}: {g.ProjectName}"));
        return result;
    }
}
