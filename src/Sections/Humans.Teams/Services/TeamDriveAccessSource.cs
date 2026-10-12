using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>Teams owns the membership and permission policy for its linked Drive items.</summary>
internal sealed class TeamDriveAccessSource(
    ITeamServiceRead teams,
    ITeamResourceServiceRead resources) : IGoogleDriveAccessSource
{
    public async Task<Dictionary<string, GoogleDriveAccessClaim>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default)
    {
        var teamById = await teams.GetTeamsAsync(ct);
        var resourcesByTeam = await resources.GetResourcesByTeamIdsAsync(teamById.Keys.ToList(), ct);
        var childTeamsByParent = teamById.Values
            .Where(t => t.IsActive && t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var result = new Dictionary<string, GoogleDriveAccessClaim>(StringComparer.Ordinal);

        foreach (var group in resourcesByTeam.Values.SelectMany(r => r)
            .Where(r => r.ResourceType is GoogleResourceType.DriveFolder or GoogleResourceType.DriveFile)
            .Where(r => folderId is null || string.Equals(r.GoogleId, folderId, StringComparison.Ordinal))
            .GroupBy(r => r.GoogleId, StringComparer.Ordinal))
        {
            var access = new Dictionary<Guid, DrivePermissionLevel>();
            var memberTeams = new Dictionary<Guid, List<TeamLink>>();
            var links = new List<TeamLink>();
            foreach (var resource in group)
            {
                if (!teamById.TryGetValue(resource.TeamId, out var team))
                    continue;
                var link = new TeamLink(team.Name, team.Slug,
                    resource.DrivePermissionLevel == DrivePermissionLevel.None ? null : resource.DrivePermissionLevel.ToString());
                links.Add(link);
                if (!team.IsActive)
                    continue;
                // Unset legacy Drive levels retain the existing Contributor grant default.
                var level = resource.DrivePermissionLevel == DrivePermissionLevel.None
                    ? DrivePermissionLevel.Contributor : resource.DrivePermissionLevel;
                foreach (var memberTeam in new[] { team }.Concat(childTeamsByParent.GetValueOrDefault(team.Id) ?? []))
                {
                    var memberLink = link with { Name = memberTeam.Name, Slug = memberTeam.Slug };
                    foreach (var member in memberTeam.Members)
                    {
                        if (!access.TryGetValue(member.UserId, out var current) || current < level)
                            access[member.UserId] = level;
                        if (!memberTeams.TryGetValue(member.UserId, out var memberLinks))
                            memberTeams[member.UserId] = memberLinks = [];
                        if (!memberLinks.Contains(memberLink))
                            memberLinks.Add(memberLink);
                    }
                }
            }
            result[group.Key] = new GoogleDriveAccessClaim(access)
            {
                AllowSharedAccess = true,
                PreserveHigherPermissions = true,
                PreserveInheritedPermissions = true,
                Resources = group.ToList(),
                RetiredResourceIds = group.Where(r => teamById.TryGetValue(r.TeamId, out var owner) && !owner.IsActive)
                    .Select(r => r.Id).ToHashSet(),
                LinkedTeams = links.DistinctBy(l => l.Slug, StringComparer.Ordinal).ToList(),
                MemberTeams = memberTeams
            };
        }
        return result;
    }
}
