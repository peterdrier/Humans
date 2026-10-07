using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>Teams owns the membership and permission union for its linked Drive resources.</summary>
internal sealed class TeamDriveAccessSource(
    ITeamServiceRead teams,
    ITeamResourceServiceRead resources) : IGoogleDriveAccessSource
{
    public async Task<Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default)
    {
        var teamInfos = await teams.GetTeamsAsync(ct);
        var resourceCounts = await resources.GetActiveResourceCountsByTeamAsync(ct);
        var resourceTeamIds = teamInfos.Keys.Concat(resourceCounts.Keys).Distinct().ToList();
        var resourcesByTeam = await resources.GetResourcesByTeamIdsAsync(resourceTeamIds, ct);
        var claims = new Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>(StringComparer.Ordinal);
        var children = teamInfos.Values.Where(t => t.IsActive && t.ParentTeamId.HasValue)
            .ToLookup(t => t.ParentTeamId!.Value);

        foreach (var resource in resourcesByTeam.Values.SelectMany(r => r))
        {
            if (!resource.IsActive || resource.ResourceType == GoogleResourceType.Group
                || (folderId is not null && !string.Equals(folderId, resource.GoogleId, StringComparison.Ordinal)))
                continue;

            if (!claims.TryGetValue(resource.GoogleId, out var access))
                claims[resource.GoogleId] = access = [];

            // Claim inactive teams' resources with no grants until successful reconciliation
            // retires their resource rows. An active team can share the same Google id.
            if (!teamInfos.TryGetValue(resource.TeamId, out var team) || !team.IsActive
                || resource.DrivePermissionLevel == DrivePermissionLevel.None)
                continue;

            var users = team.Members.Select(m => m.UserId)
                .Concat(children[team.Id].SelectMany(t => t.Members.Select(m => m.UserId)));
            foreach (var userId in users)
            {
                if (!access.TryGetValue(userId, out var existing) || resource.DrivePermissionLevel > existing)
                    access[userId] = resource.DrivePermissionLevel;
            }
        }

        return claims;
    }
}
