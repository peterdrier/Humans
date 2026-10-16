using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>Teams owns membership, child-team rollup and the union of shared-resource claims.</summary>
internal sealed class TeamDriveAccessSource(
    ITeamServiceRead teams,
    ITeamResourceServiceRead resources) : IGoogleDriveAccessSource
{
    public async Task<Dictionary<string, GoogleDriveAccessClaim>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default)
    {
        var teamsById = await teams.GetTeamsAsync(ct);
        var resourcesByTeam = await resources.GetResourcesByTeamIdsAsync(teamsById.Keys.ToList(), ct);
        var childrenByParent = teamsById.Values.Where(t => t.IsActive && t.ParentTeamId.HasValue)
            .ToLookup(t => t.ParentTeamId!.Value);
        var claims = new Dictionary<string, GoogleDriveAccessClaim>(StringComparer.Ordinal);
        foreach (var group in resourcesByTeam.Values.SelectMany(r => r)
            .Where(r => r.IsActive && r.ResourceType != GoogleResourceType.Group
                && (folderId is null || string.Equals(r.GoogleId, folderId, StringComparison.Ordinal)))
            .GroupBy(r => r.GoogleId, StringComparer.Ordinal))
        {
            var access = new Dictionary<Guid, DrivePermissionLevel>();
            var linkedTeams = new List<TeamLink>();
            var memberLinks = new Dictionary<Guid, List<TeamLink>>();
            foreach (var resource in group)
            {
                if (!teamsById.TryGetValue(resource.TeamId, out var team)) continue;
                var level = resource.DrivePermissionLevel;
                var link = new TeamLink(team.Name, team.Slug,
                    level == DrivePermissionLevel.None ? null : level.ToString());
                if (!linkedTeams.Any(existing => string.Equals(existing.Slug, link.Slug, StringComparison.Ordinal)))
                    linkedTeams.Add(link);
                if (!team.IsActive || level == DrivePermissionLevel.None) continue;

                foreach (var memberTeam in new[] { team }.Concat(childrenByParent[team.Id]))
                {
                    var memberLink = new TeamLink(memberTeam.Name, memberTeam.Slug, level.ToString());
                    foreach (var member in memberTeam.Members)
                    {
                        if (!access.TryGetValue(member.UserId, out var existingLevel) || level > existingLevel)
                            access[member.UserId] = level;
                        if (!memberLinks.TryGetValue(member.UserId, out var links))
                            memberLinks[member.UserId] = links = [];
                        var existing = links.FindIndex(l => string.Equals(l.Slug, memberLink.Slug, StringComparison.Ordinal));
                        if (existing < 0) links.Add(memberLink);
                        else if (Enum.TryParse<DrivePermissionLevel>(links[existing].PermissionLevel, out var oldLevel)
                            && level > oldLevel) links[existing] = memberLink;
                    }
                }
            }
            claims[group.Key] = new(access, linkedTeams,
                memberLinks.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<TeamLink>)entry.Value));
        }
        return claims;
    }
}
