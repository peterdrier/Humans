using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Each body's Google group and files. Until the switch to Governance's own Google claims, the copy from the
/// body's old system team is pending whenever it differs from Teams, so a re-apply picks up later edits there.
/// </summary>
internal sealed class GovernanceBodyLinksService(
    IGovernanceBodyRepository repository,
    ITeamService teams,
    ITeamResourceService teamResources,
    IAuditLogService audit,
    IClock clock) : IApplicationService
{
    /// <summary>The old system team per body; its Guid also keys the body's audit entries.</summary>
    internal static readonly IReadOnlyDictionary<GovernanceBody, Guid> TeamIds = new Dictionary<GovernanceBody, Guid>
    {
        [GovernanceBody.Asociados] = SystemTeamIds.Asociados,
        [GovernanceBody.Colaboradores] = SystemTeamIds.Colaboradors,
        [GovernanceBody.Board] = SystemTeamIds.Board,
    };

    public Task<IReadOnlyList<GovernanceBodyLinks>> GetAllAsync(CancellationToken ct = default) =>
        repository.GetAllAsync(ct);

    /// <summary>The bodies whose stored links differ from their old team's, as (stored, from Teams).</summary>
    public async Task<IReadOnlyList<(GovernanceBodyLinks? Stored, GovernanceBodyLinks FromTeams)>> GetPendingCopiesAsync(
        CancellationToken ct = default)
    {
        var stored = (await repository.GetAllAsync(ct)).ToDictionary(b => b.Body);
        var fromTeams = await ReadFromTeamsAsync(ct);
        return [.. fromTeams
            .Select(t => (Stored: stored.GetValueOrDefault(t.Body), FromTeams: t))
            .Where(p => p.Stored is null || !SameLinks(p.Stored, p.FromTeams))];
    }

    /// <summary>Overwrites every pending body with its old team's links. Returns how many bodies it wrote.</summary>
    public async Task<int> CopyFromTeamsAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var pending = await GetPendingCopiesAsync(ct);
        foreach (var (_, links) in pending)
        {
            await repository.ReplaceAsync(links, ct);
            await audit.LogAsync(
                GovernanceAuditActions.GovernanceBodyLinksCopied, AuditEntityTypes.GovernanceBody, TeamIds[links.Body],
                $"Copied {links.Body} group '{links.GoogleGroupPrefix ?? "none"}' and {links.Files.Count} Google file(s) from its system team",
                actorUserId);
        }

        return pending.Count;
    }

    private async Task<IReadOnlyList<GovernanceBodyLinks>> ReadFromTeamsAsync(CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var resources = await teamResources.GetResourcesByTeamIdsAsync([.. TeamIds.Values], ct);
        var result = new List<GovernanceBodyLinks>();
        foreach (var (body, teamId) in TeamIds)
        {
            var team = await teams.GetTeamAsync(teamId, ct);
            if (team is null)
            {
                continue; // a missing team must not wipe the stored copy
            }

            var links = new GovernanceBodyLinks { Body = body, GoogleGroupPrefix = team.GoogleGroupPrefix, UpdatedAt = now };
            // The group address lives on the team; a Group resource row only records the link.
            links.Files.AddRange(resources.GetValueOrDefault(teamId, [])
                .Where(r => r.IsActive && r.ResourceType != GoogleResourceType.Group)
                .Select(r => new GovernanceBodyFile
                {
                    Id = Guid.NewGuid(),
                    Body = body,
                    GoogleId = r.GoogleId,
                    ResourceType = r.ResourceType,
                    Name = r.Name,
                    Url = r.Url,
                    PermissionLevel = r.DrivePermissionLevel,
                }));
            result.Add(links);
        }

        return result;
    }

    private static bool SameLinks(GovernanceBodyLinks a, GovernanceBodyLinks b) =>
        string.Equals(a.GoogleGroupPrefix, b.GoogleGroupPrefix, StringComparison.Ordinal)
        && FileKeys(a).SetEquals(FileKeys(b));

    private static HashSet<(string, GoogleResourceType, string, string?, DrivePermissionLevel)> FileKeys(GovernanceBodyLinks links) =>
        [.. links.Files.Select(f => (f.GoogleId, f.ResourceType, f.Name, f.Url, f.PermissionLevel))];
}
