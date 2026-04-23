using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.Teams;

/// <summary>
/// EF-backed implementation of <see cref="ITeamRepository"/>. The only
/// non-test file that touches <c>DbContext.Teams</c>, <c>DbContext.TeamMembers</c>,
/// <c>DbContext.TeamJoinRequests</c>, <c>DbContext.TeamRoleAssignments</c>,
/// or <c>DbSet&lt;TeamRoleDefinition&gt;</c> after the Teams §15 migration.
/// <para>
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton.
/// </para>
/// </summary>
public sealed class TeamRepository : ITeamRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public TeamRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Team reads
    // ==========================================================================

    public async Task<Team?> GetByIdAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == teamId, ct);
    }

    public async Task<Team?> GetByIdWithRelationsAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .Include(t => t.ParentTeam)
            .Include(t => t.ChildTeams)
            .FirstOrDefaultAsync(t => t.Id == teamId, ct);
    }

    public async Task<Team?> FindForMutationAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams.FindAsync(new object[] { teamId }, ct);
    }

    public async Task<Team?> GetBySlugWithRelationsAsync(string normalizedSlug, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .Include(t => t.ParentTeam)
            .Include(t => t.ChildTeams)
            .FirstOrDefaultAsync(t => t.Slug == normalizedSlug || t.CustomSlug == normalizedSlug, ct);
    }

    public async Task<bool> SlugExistsAsync(string slug, Guid? excludingTeamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.Teams.AsNoTracking().Where(t => t.Slug == slug || t.CustomSlug == slug);
        if (excludingTeamId.HasValue)
            q = q.Where(t => t.Id != excludingTeamId.Value);
        return await q.AnyAsync(ct);
    }

    public async Task<IReadOnlyList<Team>> GetAllActiveAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .Include(t => t.ChildTeams)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Team>> GetAllActiveWithMembersAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Where(t => t.IsActive)
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Team>> GetAllActiveUserCreatedAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None)
            .OrderBy(t => t.Name)
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamOptionDto>> GetActiveOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new TeamOptionDto(t.Id, t.Name))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamOptionDto>> GetBudgetableOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Where(t => t.HasBudget && t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new TeamOptionDto(t.Id, t.Name))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return new Dictionary<Guid, string>();

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
    }

    public async Task<string?> GetNameByGoogleGroupPrefixAsync(string prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        var escaped = EscapeLikePattern(prefix.Trim());

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams
            .AsNoTracking()
            .Where(t => t.GoogleGroupPrefix != null
                && EF.Functions.ILike(t.GoogleGroupPrefix, escaped, "\\"))
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    public async Task<(IReadOnlyList<Team> Items, int TotalCount)> GetAllForAdminAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var baseQuery = db.Teams
            .AsNoTracking()
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .Include(t => t.JoinRequests.Where(r => r.Status == TeamJoinRequestStatus.Pending))
            .Include(t => t.RoleDefinitions)
            .OrderBy(t => t.SystemTeamType)
            .ThenBy(t => t.Name);

        var totalCount = await baseQuery.CountAsync(ct);

        var items = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<IReadOnlyDictionary<Guid, Team>> GetByIdsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return new Dictionary<Guid, Team>();

        await using var db = await _factory.CreateDbContextAsync(ct);

        var requested = await db.Teams
            .AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToListAsync(ct);

        var parentIds = requested
            .Where(t => t.ParentTeamId.HasValue)
            .Select(t => t.ParentTeamId!.Value)
            .Distinct()
            .Where(id => !teamIds.Contains(id))
            .ToList();

        var parents = parentIds.Count == 0
            ? new List<Team>()
            : await db.Teams
                .AsNoTracking()
                .Where(t => parentIds.Contains(t.Id))
                .ToListAsync(ct);

        var dict = new Dictionary<Guid, Team>(requested.Count + parents.Count);
        foreach (var t in requested) dict[t.Id] = t;
        foreach (var t in parents) dict[t.Id] = t;
        return dict;
    }

    public async Task<bool> HasActiveChildrenAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Teams.AnyAsync(t => t.ParentTeamId == teamId && t.IsActive, ct);
    }

    public async Task<IReadOnlyList<(Guid ChildId, Guid ParentId)>> GetActiveChildIdsByParentsAsync(
        IReadOnlyCollection<Guid> parentTeamIds, CancellationToken ct = default)
    {
        if (parentTeamIds.Count == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Teams
            .AsNoTracking()
            .Where(t => t.ParentTeamId != null && parentTeamIds.Contains(t.ParentTeamId.Value) && t.IsActive)
            .Select(t => new { t.Id, t.ParentTeamId })
            .ToListAsync(ct);

        return rows.Select(r => (r.Id, r.ParentTeamId!.Value)).ToList();
    }

    public async Task AddTeamAsync(Team team, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateTeamAsync(Team team, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Teams.Update(team);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> AddTeamWithRequiresApprovalOverrideAsync(
        Team team, bool requiresApproval, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Teams.Add(team);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Unique-constraint collision — typically a slug race against a
            // concurrent create of the same name. Return false so the service
            // can retry with the next suffix. Detach the tracked entity so the
            // caller can reuse the context-free Team instance if needed.
            db.Entry(team).State = EntityState.Detached;
            return false;
        }

        if (!requiresApproval)
        {
            // RequiresApproval has a store default of true, so persist explicit false
            // after insert instead of relying on EF's insert sentinel handling.
            var entry = db.Entry(team).Property(t => t.RequiresApproval);
            entry.CurrentValue = false;
            entry.IsModified = true;
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<int> DeactivateTeamAsync(Guid teamId, Instant now, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var team = await db.Teams.FindAsync(new object[] { teamId }, ct)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        var activeMembers = await db.TeamMembers
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null)
            .ToListAsync(ct);

        foreach (var member in activeMembers)
            member.LeftAt = now;

        team.IsActive = false;
        team.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return activeMembers.Count;
    }

    public async Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var team = await db.Teams.FindAsync(new object[] { teamId }, ct);
        if (team is null)
            return (false, null);

        var previous = team.GoogleGroupPrefix;
        team.GoogleGroupPrefix = prefix;
        await db.SaveChangesAsync(ct);
        return (true, previous);
    }

    // ==========================================================================
    // TeamMember reads / writes
    // ==========================================================================

    public async Task<IReadOnlyList<TeamMember>> GetActiveByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == userId && tm.LeftAt == null)
            .Include(tm => tm.Team)
                .ThenInclude(t => t.ParentTeam)
            .OrderBy(tm => tm.Team.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamMember>> GetActiveMembersAsync(
        Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null)
            .OrderBy(tm => tm.Role)
            .ThenBy(tm => tm.JoinedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveMemberUserIdsAsync(
        Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null)
            .Select(tm => tm.UserId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetCoordinatorUserIdsAsync(
        Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.TeamId == teamId
                && tm.LeftAt == null
                && tm.Role == TeamMemberRole.Coordinator)
            .Select(tm => tm.UserId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamCoordinatorRef>> GetActiveCoordinatorsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Where(tm => teamIds.Contains(tm.TeamId)
                         && tm.LeftAt == null
                         && tm.Role == TeamMemberRole.Coordinator)
            .Select(tm => new TeamCoordinatorRef(tm.TeamId, tm.UserId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamMember>> GetActiveMembersForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Include(tm => tm.Team)
            .Where(tm => teamIds.Contains(tm.TeamId) && tm.LeftAt == null)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamMember>> GetActiveChildMembersByParentIdsAsync(
        IReadOnlyCollection<Guid> parentTeamIds, CancellationToken ct = default)
    {
        if (parentTeamIds.Count == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Include(tm => tm.Team)
            .Where(tm =>
                tm.Team.ParentTeamId != null
                && parentTeamIds.Contains(tm.Team.ParentTeamId!.Value)
                && tm.Team.IsActive
                && tm.LeftAt == null)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetUserCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var byRoleAssignment = await db.TeamRoleAssignments
            .AsNoTracking()
            .Where(tra =>
                tra.TeamMember.UserId == userId &&
                tra.TeamMember.LeftAt == null &&
                tra.TeamRoleDefinition.IsManagement &&
                tra.TeamRoleDefinition.Team.SystemTeamType == SystemTeamType.None)
            .Select(tra => tra.TeamRoleDefinition.TeamId)
            .ToListAsync(ct);

        var byMemberRole = await db.TeamMembers
            .AsNoTracking()
            .Where(tm =>
                tm.UserId == userId &&
                tm.LeftAt == null &&
                tm.Role == TeamMemberRole.Coordinator &&
                tm.Team.SystemTeamType == SystemTeamType.None)
            .Select(tm => tm.TeamId)
            .ToListAsync(ct);

        return byRoleAssignment.Union(byMemberRole).Distinct().ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetUserDepartmentCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var directDepartmentIds = await db.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == userId && tm.LeftAt == null
                && tm.Role == TeamMemberRole.Coordinator
                && tm.Team.ParentTeamId == null)
            .Select(tm => tm.TeamId)
            .ToListAsync(ct);

        var mgmtDepartmentIds = await db.TeamRoleAssignments
            .AsNoTracking()
            .Where(tra =>
                tra.TeamMember.UserId == userId &&
                tra.TeamMember.LeftAt == null &&
                tra.TeamRoleDefinition.IsManagement &&
                tra.TeamRoleDefinition.Team.ParentTeamId == null)
            .Select(tra => tra.TeamMember.TeamId)
            .ToListAsync(ct);

        return directDepartmentIds.Union(mgmtDepartmentIds).Distinct().ToList();
    }

    public async Task<bool> IsActiveMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .AnyAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, ct);
    }

    public async Task<TeamMember?> FindActiveMemberForMutationAsync(
        Guid teamId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, ct);
    }

    public async Task AddMemberAsync(TeamMember member, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TeamMembers.Add(member);
        await db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        // No-op at the interface level: repository methods each open/close
        // their own context. This method exists purely to satisfy compound
        // writes in the service that still want an explicit commit point
        // after the repository has bundled multiple mutations in one method.
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetActiveNonSystemTeamNamesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<string>>();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.TeamMembers
            .AsNoTracking()
            .Where(tm => userIds.Contains(tm.UserId) && tm.LeftAt == null && !tm.Team.IsSystemTeam)
            .Select(tm => new { tm.UserId, TeamName = tm.Team.Name })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .Select(r => r.TeamName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList());
    }

    // ==========================================================================
    // TeamJoinRequest reads / writes
    // ==========================================================================

    public async Task<TeamJoinRequest?> FindUserPendingRequestAsync(
        Guid teamId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamJoinRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == userId
                && r.Status == TeamJoinRequestStatus.Pending, ct);
    }

    public async Task<TeamJoinRequest?> FindRequestForMutationAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamJoinRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
    }

    public async Task<TeamJoinRequest?> FindRequestWithTeamForMutationAsync(
        Guid requestId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamJoinRequests
            .Include(r => r.Team)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetAllPendingWithTeamsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamJoinRequests
            .AsNoTracking()
            .Include(r => r.Team)
            .Where(r => r.Status == TeamJoinRequestStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetPendingForTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamJoinRequests
            .AsNoTracking()
            .Include(r => r.Team)
            .Where(r => teamIds.Contains(r.TeamId) && r.Status == TeamJoinRequestStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetPendingForTeamAsync(
        Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamJoinRequests
            .AsNoTracking()
            .Where(r => r.TeamId == teamId && r.Status == TeamJoinRequestStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetPendingCountsByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return new Dictionary<Guid, int>();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var counts = await db.TeamJoinRequests
            .Where(r => teamIds.Contains(r.TeamId) && r.Status == TeamJoinRequestStatus.Pending)
            .GroupBy(r => r.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var result = teamIds.ToDictionary(id => id, _ => 0);
        foreach (var item in counts)
            result[item.TeamId] = item.Count;
        return result;
    }

    public async Task<int> GetTotalPendingCountAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamJoinRequests
            .CountAsync(r => r.Status == TeamJoinRequestStatus.Pending, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveNonSystemTeamCoordinatorUserIdsAsync(
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.LeftAt == null
                && tm.Role == TeamMemberRole.Coordinator
                && tm.Team.SystemTeamType == SystemTeamType.None)
            .Select(tm => tm.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task AddRequestAsync(TeamJoinRequest request, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TeamJoinRequests.Add(request);
        await db.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // TeamRoleDefinition
    // ==========================================================================

    public async Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleDefinition>()
            .Include(d => d.Assignments)
                .ThenInclude(a => a.TeamMember)
            .Where(d => d.TeamId == teamId)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
                .ThenInclude(a => a.TeamMember)
            .Where(d => d.Team.IsActive && d.Team.SystemTeamType == SystemTeamType.None)
            .OrderBy(d => d.Team.Name).ThenBy(d => d.SortOrder).ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<TeamRoleDefinition?> FindRoleDefinitionForMutationAsync(
        Guid roleDefinitionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
                .ThenInclude(a => a.TeamMember)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, ct);
    }

    public async Task<TeamRoleDefinition?> FindRoleDefinitionWithMembersForMutationAsync(
        Guid roleDefinitionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
                .ThenInclude(a => a.TeamMember)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, ct);
    }

    public async Task<bool> RoleDefinitionNameExistsAsync(
        Guid teamId, string lowerName, Guid? excludingId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.Set<TeamRoleDefinition>()
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            .Where(d => d.TeamId == teamId && d.Name.ToLower() == lowerName);
#pragma warning restore MA0011
        if (excludingId.HasValue)
            q = q.Where(d => d.Id != excludingId.Value);
        return await q.AnyAsync(ct);
    }

    public async Task<bool> OtherRoleHasIsManagementAsync(
        Guid teamId, Guid excludingRoleDefinitionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleDefinition>()
            .AnyAsync(d => d.TeamId == teamId
                && d.Id != excludingRoleDefinitionId
                && d.IsManagement, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetPublicManagementRoleNamesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return new Dictionary<Guid, string>();

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleDefinition>()
            .AsNoTracking()
            .Where(d => teamIds.Contains(d.TeamId) && d.IsManagement && d.IsPublic)
            .ToDictionaryAsync(d => d.TeamId, d => d.Name, ct);
    }

    public async Task AddRoleDefinitionAsync(TeamRoleDefinition definition, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Set<TeamRoleDefinition>().Add(definition);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveRoleDefinitionAsync(TeamRoleDefinition definition, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Set<TeamRoleDefinition>().Attach(definition);
        db.Set<TeamRoleDefinition>().Remove(definition);
        await db.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // TeamRoleAssignment
    // ==========================================================================

    public async Task<IReadOnlyList<TeamRoleAssignment>> FindAssignmentsForMemberForMutationAsync(
        Guid teamMemberId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleAssignment>()
            .Include(a => a.TeamRoleDefinition)
                .ThenInclude(d => d.Team)
            .Where(a => a.TeamMemberId == teamMemberId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamRoleAssignment>> FindAssignmentsForMembersForMutationAsync(
        IReadOnlyCollection<Guid> teamMemberIds, CancellationToken ct = default)
    {
        if (teamMemberIds.Count == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleAssignment>()
            .Include(a => a.TeamRoleDefinition)
                .ThenInclude(d => d.Team)
            .Where(a => teamMemberIds.Contains(a.TeamMemberId))
            .ToListAsync(ct);
    }

    public async Task<bool> MemberHasOtherManagementAssignmentAsync(
        Guid teamMemberId, Guid excludingAssignmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleAssignment>()
            .AnyAsync(a => a.TeamMemberId == teamMemberId
                && a.Id != excludingAssignmentId
                && a.TeamRoleDefinition.IsManagement, ct);
    }

    public async Task<TeamRoleAssignment?> FindAssignmentForMutationAsync(
        Guid roleDefinitionId, Guid teamMemberId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleAssignment>()
            .Include(a => a.TeamMember)
            .FirstOrDefaultAsync(a => a.TeamRoleDefinitionId == roleDefinitionId
                && a.TeamMemberId == teamMemberId, ct);
    }

    public async Task AddAssignmentAsync(TeamRoleAssignment assignment, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Set<TeamRoleAssignment>().Add(assignment);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsWithManagementOnTeamAsync(
        Guid teamId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TeamRoleAssignment>()
            .Where(a => a.TeamRoleDefinition.TeamId == teamId
                && a.TeamRoleDefinition.IsManagement)
            .Select(a => a.TeamMember.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Bulk / seed / revoke
    // ==========================================================================

    public async Task<bool> AddMemberWithOutboxAsync(
        TeamMember member, GoogleSyncOutboxEvent outboxEvent, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TeamMembers.Add(member);
        db.GoogleSyncOutboxEvents.Add(outboxEvent);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return false;
        }
    }

    public async Task<bool> ApproveRequestWithMemberAsync(
        TeamJoinRequest request,
        TeamMember member,
        GoogleSyncOutboxEvent? outboxEvent,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TeamJoinRequests.Attach(request);
        db.Entry(request).State = EntityState.Modified;
        db.TeamMembers.Add(member);
        if (outboxEvent is not null)
            db.GoogleSyncOutboxEvents.Add(outboxEvent);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<TeamRoleAssignment>> MarkMemberLeftWithOutboxAsync(
        Guid teamMemberId, Instant leftAt, GoogleSyncOutboxEvent? outboxEvent, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var member = await db.TeamMembers.FirstOrDefaultAsync(tm => tm.Id == teamMemberId, ct)
            ?? throw new InvalidOperationException("Team member not found");

        var roleAssignments = await db.Set<TeamRoleAssignment>()
            .Include(a => a.TeamRoleDefinition)
                .ThenInclude(d => d.Team)
            .Where(a => a.TeamMemberId == teamMemberId)
            .ToListAsync(ct);

        if (roleAssignments.Count > 0)
            db.Set<TeamRoleAssignment>().RemoveRange(roleAssignments);

        member.LeftAt = leftAt;

        if (outboxEvent is not null)
            db.GoogleSyncOutboxEvents.Add(outboxEvent);

        await db.SaveChangesAsync(ct);
        return roleAssignments;
    }

    public async Task<TeamMemberRole?> SetMemberRoleAsync(
        Guid teamId, Guid userId, TeamMemberRole role, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var member = await db.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, ct);

        if (member is null || member.Role == role)
            return null;

        member.Role = role;
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task<bool> WithdrawRequestAsync(
        Guid requestId, Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var request = await db.TeamJoinRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.UserId == userId, ct);
        if (request is null || request.Status != TeamJoinRequestStatus.Pending)
            return false;

        request.Status = TeamJoinRequestStatus.Withdrawn;
        request.ResolvedAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RejectRequestAsync(
        Guid requestId, Guid reviewerUserId, string reason, Instant now, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var request = await db.TeamJoinRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null || request.Status != TeamJoinRequestStatus.Pending)
            return false;

        request.Status = TeamJoinRequestStatus.Rejected;
        request.ReviewedByUserId = reviewerUserId;
        request.ReviewNotes = reason;
        request.ResolvedAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(TeamRoleAssignment Assignment, bool AutoAddedToTeam, TeamMember Member)> AssignToRoleAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        TeamMember? autoAddMember,
        GoogleSyncOutboxEvent? outboxEvent,
        bool promoteToCoordinator,
        Instant now,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var definition = await db.Set<TeamRoleDefinition>()
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, ct)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        TeamMember teamMember;
        var autoAddedToTeam = false;

        if (autoAddMember is not null)
        {
            teamMember = autoAddMember;
            db.TeamMembers.Add(teamMember);
            autoAddedToTeam = true;

            var pendingRequest = await db.TeamJoinRequests
                .FirstOrDefaultAsync(r => r.TeamId == definition.TeamId
                    && r.UserId == targetUserId
                    && r.Status == TeamJoinRequestStatus.Pending, ct);
            if (pendingRequest is not null)
            {
                pendingRequest.Status = TeamJoinRequestStatus.Approved;
                pendingRequest.ReviewedByUserId = actorUserId;
                pendingRequest.ReviewNotes = "Added via role assignment";
                pendingRequest.ResolvedAt = now;
            }

            if (outboxEvent is not null)
                db.GoogleSyncOutboxEvents.Add(outboxEvent);
        }
        else
        {
            teamMember = await db.TeamMembers
                .FirstOrDefaultAsync(tm => tm.TeamId == definition.TeamId && tm.UserId == targetUserId && tm.LeftAt == null, ct)
                ?? throw new InvalidOperationException("Team member not found");
        }

        if (definition.Assignments.Any(a => a.TeamMemberId == teamMember.Id))
            throw new InvalidOperationException("User is already assigned to this role");

        if (definition.Assignments.Count >= definition.SlotCount)
            throw new InvalidOperationException($"All {definition.SlotCount} slots for role are filled");

        var usedSlots = definition.Assignments.Select(a => a.SlotIndex).ToHashSet();
        var nextSlotIndex = Enumerable.Range(0, definition.SlotCount).First(i => !usedSlots.Contains(i));

        var assignment = new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = roleDefinitionId,
            TeamMemberId = teamMember.Id,
            SlotIndex = nextSlotIndex,
            AssignedAt = now,
            AssignedByUserId = actorUserId
        };
        db.Set<TeamRoleAssignment>().Add(assignment);

        if (promoteToCoordinator && teamMember.Role != TeamMemberRole.Coordinator)
            teamMember.Role = TeamMemberRole.Coordinator;

        await db.SaveChangesAsync(ct);
        return (assignment, autoAddedToTeam, teamMember);
    }

    public async Task<(bool Demoted, Guid TargetUserId)> UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var definition = await db.Set<TeamRoleDefinition>()
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, ct)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var assignment = await db.Set<TeamRoleAssignment>()
            .Include(a => a.TeamMember)
            .FirstOrDefaultAsync(a => a.TeamRoleDefinitionId == roleDefinitionId && a.TeamMemberId == teamMemberId, ct)
            ?? throw new InvalidOperationException("Assignment not found");

        db.Set<TeamRoleAssignment>().Remove(assignment);

        var demoted = false;
        if (definition.IsManagement)
        {
            var hasOtherManagementAssignments = await db.Set<TeamRoleAssignment>()
                .AnyAsync(a => a.TeamMemberId == teamMemberId
                    && a.Id != assignment.Id
                    && a.TeamRoleDefinition.IsManagement, ct);
            if (!hasOtherManagementAssignments && assignment.TeamMember.Role == TeamMemberRole.Coordinator)
            {
                assignment.TeamMember.Role = TeamMemberRole.Member;
                demoted = true;
            }
        }

        await db.SaveChangesAsync(ct);
        return (demoted, assignment.TeamMember.UserId);
    }

    public async Task<(IReadOnlyList<Guid> DemotedUserIds, bool DemotedMembers)> PersistRoleDefinitionUpdateAsync(
        TeamRoleDefinition definition,
        bool clearingIsManagement,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // Attach the definition as modified (scalar properties updated in memory).
        db.Set<TeamRoleDefinition>().Attach(definition);
        db.Entry(definition).State = EntityState.Modified;

        var demotedUserIds = new List<Guid>();
        var demotedMembers = false;

        if (clearingIsManagement)
        {
            var assignedMemberIds = await db.Set<TeamRoleAssignment>()
                .Where(a => a.TeamRoleDefinitionId == definition.Id)
                .Select(a => a.TeamMemberId)
                .ToListAsync(ct);

            if (assignedMemberIds.Count > 0)
            {
                var members = await db.TeamMembers
                    .Where(m => assignedMemberIds.Contains(m.Id) && m.Role == TeamMemberRole.Coordinator)
                    .ToListAsync(ct);

                foreach (var member in members)
                {
                    var hasOtherManagement = await db.Set<TeamRoleAssignment>()
                        .AnyAsync(a => a.TeamMemberId == member.Id
                            && a.TeamRoleDefinitionId != definition.Id
                            && a.TeamRoleDefinition.IsManagement, ct);
                    if (!hasOtherManagement)
                    {
                        member.Role = TeamMemberRole.Member;
                        demotedMembers = true;
                        demotedUserIds.Add(member.UserId);
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return (demotedUserIds, demotedMembers);
    }

    public async Task<bool> PersistRoleIsManagementAsync(
        TeamRoleDefinition definition,
        bool clearingIsManagement,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        db.Set<TeamRoleDefinition>().Attach(definition);
        db.Entry(definition).State = EntityState.Modified;

        var demoted = false;
        if (clearingIsManagement)
        {
            var assignedMemberIds = await db.Set<TeamRoleAssignment>()
                .Where(a => a.TeamRoleDefinitionId == definition.Id)
                .Select(a => a.TeamMemberId)
                .ToListAsync(ct);

            if (assignedMemberIds.Count > 0)
            {
                var members = await db.TeamMembers
                    .Where(m => assignedMemberIds.Contains(m.Id) && m.Role == TeamMemberRole.Coordinator)
                    .ToListAsync(ct);

                foreach (var member in members)
                {
                    var hasOtherManagement = await db.Set<TeamRoleAssignment>()
                        .AnyAsync(a => a.TeamMemberId == member.Id
                            && a.TeamRoleDefinitionId != definition.Id
                            && a.TeamRoleDefinition.IsManagement, ct);
                    if (!hasOtherManagement)
                    {
                        member.Role = TeamMemberRole.Member;
                        demoted = true;
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return demoted;
    }

    public async Task<int> RevokeAllMembershipsAsync(
        Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var activeMembers = await db.TeamMembers
            .Where(tm => tm.UserId == userId && tm.LeftAt == null)
            .ToListAsync(ct);

        if (activeMembers.Count == 0)
            return 0;

        var activeMemberIds = activeMembers.Select(m => m.Id).ToList();

        var roleAssignments = await db.Set<TeamRoleAssignment>()
            .Where(a => activeMemberIds.Contains(a.TeamMemberId))
            .ToListAsync(ct);
        if (roleAssignments.Count > 0)
            db.Set<TeamRoleAssignment>().RemoveRange(roleAssignments);

        foreach (var membership in activeMembers)
            membership.LeftAt = now;

        await db.SaveChangesAsync(ct);
        return activeMembers.Count;
    }

    public async Task<int> EnqueueResyncEventsForUserAsync(
        Guid userId, Instant now, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var memberships = await db.TeamMembers
            .Where(tm => tm.UserId == userId && tm.LeftAt == null)
            .Select(tm => new { tm.Id, tm.TeamId })
            .ToListAsync(ct);

        if (memberships.Count == 0)
            return 0;

        foreach (var membership in memberships)
        {
            var dedupeKey = $"{membership.Id}:{Humans.Domain.Constants.GoogleSyncOutboxEventTypes.AddUserToTeamResources}:resync:{now}";
            db.GoogleSyncOutboxEvents.Add(new GoogleSyncOutboxEvent
            {
                Id = Guid.NewGuid(),
                EventType = Humans.Domain.Constants.GoogleSyncOutboxEventTypes.AddUserToTeamResources,
                TeamId = membership.TeamId,
                UserId = userId,
                OccurredAt = now,
                DeduplicationKey = dedupeKey
            });
        }

        await db.SaveChangesAsync(ct);
        return memberships.Count;
    }

    public async Task<int> HardDeleteBySuffixAsync(string nameSuffix, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var targetTeams = await db.Teams
            .Where(t => t.Name.EndsWith(nameSuffix))
            .ToListAsync(ct);

        if (targetTeams.Count == 0)
            return 0;

        var teamIds = targetTeams.Select(t => t.Id).ToList();

        await db.TeamMembers
            .Where(tm => teamIds.Contains(tm.TeamId))
            .ExecuteDeleteAsync(ct);

        await db.TeamJoinRequests
            .Where(r => teamIds.Contains(r.TeamId))
            .ExecuteDeleteAsync(ct);

        db.Teams.RemoveRange(targetTeams);
        await db.SaveChangesAsync(ct);

        return targetTeams.Count;
    }

    public async Task AddOutboxEventAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.GoogleSyncOutboxEvents.Add(outboxEvent);
        await db.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // GDPR export contribution
    // ==========================================================================

    public async Task<IReadOnlyList<TeamMember>> GetAllMembershipsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamMembers
            .AsNoTracking()
            .Include(tm => tm.Team)
            .Include(tm => tm.RoleAssignments)
                .ThenInclude(tra => tra.TeamRoleDefinition)
            .Where(tm => tm.UserId == userId)
            .OrderByDescending(tm => tm.JoinedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetAllJoinRequestsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TeamJoinRequests
            .AsNoTracking()
            .Include(tjr => tjr.Team)
            .Where(tjr => tjr.UserId == userId)
            .OrderByDescending(tjr => tjr.RequestedAt)
            .ToListAsync(ct);
    }
}
