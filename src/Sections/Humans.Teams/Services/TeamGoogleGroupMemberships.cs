using Humans.Base.Interfaces;
using Humans.Teams.Data;

namespace Humans.Teams.Services;

internal sealed class TeamGoogleGroupMemberships(
    ITeamRepository repo,
    ILogger logger) : IApplicationService
{
    public async Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default)
    {
        var requestedKey = string.IsNullOrWhiteSpace(groupKey)
            ? null
            : groupKey.Trim();

        var teams = await repo.GetAllActiveAsync(ct);
        var groupTeams = teams
            .Where(t => t.GoogleGroupEmail is not null)
            .Where(t => requestedKey is null
                || string.Equals(t.GoogleGroupEmail, requestedKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (groupTeams.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        var duplicateGroupEmails = groupTeams
            .GroupBy(t => t.GoogleGroupEmail!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicateGroupEmail in duplicateGroupEmails)
        {
            logger.LogWarning(
                "Multiple active Teams share Google group {GroupKey}; skipping this group membership claim so Google sync fails closed",
                duplicateGroupEmail);
        }

        var syncableGroupTeams = groupTeams
            .Where(t => !duplicateGroupEmails.Contains(t.GoogleGroupEmail!))
            .ToList();
        if (syncableGroupTeams.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        var groupTeamIds = syncableGroupTeams.Select(t => t.Id).ToHashSet();
        var parentIdByChildTeamId = teams
            .Where(t => t.ParentTeamId is { } parentId && groupTeamIds.Contains(parentId))
            .ToDictionary(t => t.Id, t => t.ParentTeamId!.Value);

        var childMemberUserIdsByParentTeam = teams
            .Where(t => parentIdByChildTeamId.ContainsKey(t.Id))
            .GroupBy(t => parentIdByChildTeamId[t.Id])
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(t => t.Members.Where(m => m.LeftAt is null).Select(m => m.UserId))
                    .Distinct()
                    .ToArray());

        var result = new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in syncableGroupTeams)
        {
            var directUserIds = team.Members
                .Where(m => m.LeftAt is null)
                .Select(m => m.UserId);

            var childUserIds = childMemberUserIdsByParentTeam.GetValueOrDefault(team.Id)
                ?? [];

            result[team.GoogleGroupEmail!] = directUserIds
                .Concat(childUserIds)
                .Distinct()
                .ToArray();
        }

        return result;
    }
}
