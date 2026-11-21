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
/// Each body's Google group and files. Governance claims them in Google, so its stored copy is the source of truth:
/// the copy from the body's old system team runs once per body and never overwrites a stored row.
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

    /// <summary>The bodies with no stored row yet, as (null, from Teams).</summary>
    public async Task<IReadOnlyList<(GovernanceBodyLinks? Stored, GovernanceBodyLinks FromTeams)>> GetPendingCopiesAsync(
        CancellationToken ct = default)
    {
        var stored = (await repository.GetAllAsync(ct)).ToDictionary(b => b.Body);
        var fromTeams = await ReadFromTeamsAsync(ct);
        return [.. fromTeams
            .Select(t => (Stored: stored.GetValueOrDefault(t.Body), FromTeams: t))
            .Where(p => p.Stored is null)];
    }

    /// <summary>Stores every pending body's links from its old team. Returns how many bodies it wrote.</summary>
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
}
