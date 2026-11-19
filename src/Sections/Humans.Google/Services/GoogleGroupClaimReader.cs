using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Google.Services;

internal sealed class GoogleGroupClaimReader(
    IEnumerable<IGoogleGroupMembershipSource> sources,
    ITeamResourceManagement teamResourceService,
    ITeamService teamService,
    GoogleWorkspaceOptions options,
    ILogger logger) : IApplicationService
{
    private readonly GoogleWorkspaceOptions _options = options;

    public async Task<IReadOnlyList<GroupClaim>> LoadClaimsAsync(
        string? groupKey,
        CancellationToken ct,
        GoogleSyncSource syncSource)
    {
        var claims = new List<(string SourceName, string GroupKey, Guid[] UserIds)>();

        foreach (var source in sources)
        {
            var expected = await source.GetExpectedAsync(groupKey, ct);
            foreach (var (key, userIds) in expected)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                claims.Add((source.GetType().Name, key.Trim(), userIds.Distinct().ToArray()));
            }
        }

        return claims
            .GroupBy(c => c.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupClaim(
                g.Key,
                g.Count(),
                g.Select(c => c.SourceName).Distinct(StringComparer.Ordinal).ToArray(),
                g.SelectMany(c => c.UserIds).Distinct().ToArray(),
                syncSource))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, GoogleResourceSnapshot>> LoadActiveGroupResourcesByEmailAsync(CancellationToken ct)
    {
        var counts = await teamResourceService.GetActiveResourceCountsByTeamAsync(ct);
        if (counts.Count == 0)
            return new Dictionary<string, GoogleResourceSnapshot>(StringComparer.OrdinalIgnoreCase);

        var byTeam = await teamResourceService.GetResourcesByTeamIdsAsync(counts.Keys.ToList(), ct);
        var teamIds = byTeam.Keys.ToList();
        var teamsById = new Dictionary<Guid, TeamInfo?>();
        foreach (var teamId in teamIds)
            teamsById[teamId] = await teamService.GetTeamAsync(teamId, ct);

        var resourcesByEmail = byTeam
            .SelectMany(kvp => kvp.Value.Select(resource => (TeamId: kvp.Key, Resource: resource)))
            .Where(x => x.Resource.ResourceType == GoogleResourceType.Group && x.Resource.IsActive)
            .Select(x =>
            {
                teamsById.TryGetValue(x.TeamId, out var team);
                return (x.Resource, Email: GoogleGroupKeyHelper.TryGetGroupKey(
                    x.Resource,
                    team?.GoogleGroupEmail,
                    _options.Domain));
            })
            .Where(x => x.Email is not null)
            .OrderBy(x => x.Resource.ProvisionedAt)
            .GroupBy(x => x.Email!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in resourcesByEmail.Where(g => g.Count() > 1))
        {
            var kept = group.First().Resource;
            logger.LogWarning(
                "Multiple active Google group resource rows resolve to {GroupKey}; using {ResourceId} and ignoring {DuplicateCount} duplicate rows",
                group.Key,
                kept.Id,
                group.Count() - 1);
        }

        return resourcesByEmail.ToDictionary(g => g.Key, g => g.First().Resource, StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record GroupClaim(string GroupKey, int ClaimCount, string[] SourceNames, Guid[] UserIds, GoogleSyncSource SyncSource)
    {
        public bool IsCollision => ClaimCount > 1;
    }
}
