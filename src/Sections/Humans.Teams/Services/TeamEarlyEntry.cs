using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.EarlyEntry.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed class TeamEarlyEntry(
    ITeamRepository repo,
    IAuditLogService auditLogService,
    IClock clock,
    TeamSnapshotReader snapshotReader) : IApplicationService
{
    public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
    {
        var grants = await repo.GetEarlyEntryGrantsForEnabledTeamsAsync(ct);
        return Project(grants);
    }

    public async Task<IReadOnlyList<TeamEarlyEntryGrantInfo>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var grants = await repo.GetEarlyEntryGrantsForTeamAsync(teamId, ct);
        return grants
            .Select(g => new TeamEarlyEntryGrantInfo(g.Id, g.UserId, g.EntryDate, g.ProjectName))
            .ToList();
    }

    public async Task<TeamEarlyEntryMutationResult> AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
    {
        var team = await repo.GetByIdAsync(teamId, ct);
        if (team is null)
            return new(Refusal: $"Team {teamId} not found.");
        if (!team.EarlyEntryEnabled)
            return new(Refusal: $"Early entry is not enabled for team {teamId}.");
        if (userId == Guid.Empty)
            return new(Refusal: "UserId must be a non-empty GUID.");
        var targetUserId = await snapshotReader.ResolveMembershipTargetAsync(userId, ct);
        if (targetUserId is null)
            return new(Refusal: TeamSnapshotReader.UnavailableMembershipTargetMessage);
        userId = targetUserId.Value;

        var grant = new TeamEarlyEntryGrant
        {
            TeamId = teamId,
            UserId = userId,
            EntryDate = entryDate,
            ProjectName = projectName.Trim(),
            CreatedAt = clock.GetCurrentInstant(),
            CreatedByUserId = actorUserId,
        };
        await repo.AddEarlyEntryGrantAsync(grant, ct);
        await auditLogService.LogAsync(TeamsAuditActions.EarlyEntryGranted, nameof(TeamEarlyEntryGrant), grant.Id,
            $"EE granted to {userId} on {entryDate} for \"{grant.ProjectName}\" (team {teamId})", actorUserId);
        return new();
    }

    public async Task<TeamEarlyEntryMutationResult> EditEarlyEntryGrantAsync(Guid teamId, Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repo.FindEarlyEntryGrantForMutationAsync(grantId, ct);
        if (grant is null || grant.TeamId != teamId)
            return new(Refusal: $"EE grant {grantId} not found.");
        grant.EntryDate = entryDate;
        grant.ProjectName = projectName.Trim();
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateEarlyEntryGrantAsync(grant, ct);
        await auditLogService.LogAsync(TeamsAuditActions.EarlyEntryUpdated, nameof(TeamEarlyEntryGrant), grant.Id,
            $"EE updated for {grant.UserId}: {entryDate}, \"{grant.ProjectName}\"", actorUserId);
        return new();
    }

    public async Task RemoveEarlyEntryGrantAsync(Guid teamId, Guid grantId, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repo.FindEarlyEntryGrantForMutationAsync(grantId, ct);
        if (grant is null || grant.TeamId != teamId) return; // idempotent; another team's grant counts as missing
        await repo.RemoveEarlyEntryGrantAsync(grantId, ct);
        await auditLogService.LogAsync(TeamsAuditActions.EarlyEntryRevoked, nameof(TeamEarlyEntryGrant), grantId,
            $"EE revoked for {grant.UserId} (team {grant.TeamId})", actorUserId);
    }

    public async Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct)
    {
        var grants = await repo.GetEarlyEntryGrantsForUserAsync(userId, ct);
        if (grants.Count == 0) return;
        await repo.RemoveEarlyEntryGrantsForUserAsync(userId, ct);
    }

    /// <summary>The caller must load each grant's <see cref="TeamEarlyEntryGrant.Team"/>.</summary>
    internal static IReadOnlyList<EarlyEntryGrant> Project(IReadOnlyList<TeamEarlyEntryGrant> grants)
    {
        var result = new List<EarlyEntryGrant>(grants.Count);
        foreach (var g in grants)
            result.Add(new EarlyEntryGrant(g.UserId, g.EntryDate, $"{g.Team.Name}: {g.ProjectName}"));
        return result;
    }
}
