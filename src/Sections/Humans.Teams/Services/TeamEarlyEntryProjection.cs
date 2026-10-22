using Humans.EarlyEntry.Contracts;
using Humans.Teams.Domain;

namespace Humans.Teams.Services;

/// <summary>The caller must load each grant's <see cref="TeamEarlyEntryGrant.Team"/>.</summary>
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
