using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Camps;

internal sealed partial class CampRepository
{
    public async Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.CampRoleDefinitions.AsNoTracking().AsQueryable();
        if (!includeDeactivated)
            query = query.Where(d => d.DeactivatedAt == null);
        return await query.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<CampRoleDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task<CampRoleDefinition?> GetDefinitionBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var lowered = slug.ToLowerInvariant();
        return await ctx.CampRoleDefinitions.AsNoTracking()
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            .FirstOrDefaultAsync(d => d.Slug.ToLower() == lowered, ct);
#pragma warning restore MA0011
    }

    public async Task<CampRoleDefinition?> GetSpecialDefinitionAsync(CampSpecialRole specialRole, CancellationToken ct = default)
    {
        if (specialRole == CampSpecialRole.None)
            throw new ArgumentException("CampSpecialRole.None has no special definition.", nameof(specialRole));
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.SpecialRole == specialRole, ct);
    }

    public async Task<IReadOnlyList<CampSpecialRole>> GetExistingSpecialRolesAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleDefinitions.AsNoTracking()
            .Where(d => d.SpecialRole != CampSpecialRole.None)
            .Select(d => d.SpecialRole)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetSpecialRoleHolderUserIdsAsync(
        CampSpecialRole specialRole, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Where(a => a.Definition.SpecialRole == specialRole
                && a.Definition.DeactivatedAt == null)
            .Select(a => a.CampMember.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetSpecialRoleHolderUserIdsForSeasonAsync(
        Guid campSeasonId, CampSpecialRole specialRole, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments
            .AsNoTracking()
            .Where(a => a.CampSeasonId == campSeasonId
                && a.Definition.SpecialRole == specialRole
                && a.Definition.DeactivatedAt == null)
            .Select(a => a.CampMember.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> IsSpecialRoleHolderAnywhereAsync(
        Guid userId, CampSpecialRole specialRole, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .AnyAsync(a => a.CampMember.UserId == userId
                && a.Definition.SpecialRole == specialRole
                && a.Definition.DeactivatedAt == null, ct);
    }

    public async Task<bool> DefinitionSlugExistsAsync(string slug, Guid? excludingId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var lowered = slug.ToLowerInvariant();
        var query = ctx.CampRoleDefinitions.AsNoTracking()
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            .Where(d => d.Slug.ToLower() == lowered);
#pragma warning restore MA0011
        if (excludingId is { } id)
            query = query.Where(d => d.Id != id);
        return await query.AnyAsync(ct);
    }

    public async Task<bool> DefinitionNameExistsAsync(string name, Guid? excludingId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var lowered = name.ToLowerInvariant();
        var query = ctx.CampRoleDefinitions.AsNoTracking()
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            .Where(d => d.Name.ToLower() == lowered);
#pragma warning restore MA0011
        if (excludingId is { } id)
            query = query.Where(d => d.Id != id);
        return await query.AnyAsync(ct);
    }

    public async Task AddDefinitionAsync(CampRoleDefinition definition, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampRoleDefinitions.Add(definition);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateDefinitionAsync(Guid id, Action<CampRoleDefinition> mutate, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var def = await ctx.CampRoleDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (def is null) return false;
        mutate(def);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.Definition)
            .Include(a => a.CampMember)
            .Where(a => a.CampSeasonId == campSeasonId)
            .OrderBy(a => a.Definition.SortOrder).ThenBy(a => a.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<CampRoleAssignment?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
    }

    public async Task<int> CountAssignmentsForSeasonAndDefinitionAsync(Guid campSeasonId, Guid definitionId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .CountAsync(a => a.CampSeasonId == campSeasonId && a.CampRoleDefinitionId == definitionId, ct);
    }

    public async Task<bool> AssignmentExistsAsync(Guid campSeasonId, Guid definitionId, Guid campMemberId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .AnyAsync(a => a.CampSeasonId == campSeasonId
                        && a.CampRoleDefinitionId == definitionId
                        && a.CampMemberId == campMemberId, ct);
    }

    public async Task<bool> AddAssignmentAsync(CampRoleAssignment assignment, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampRoleAssignments.Add(assignment);
        try
        {
            await ctx.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // I5 fix — unique-index race on (CampSeasonId, CampRoleDefinitionId, CampMemberId)
            return false;
        }
    }

    public async Task<bool> DeleteAssignmentAsync(Guid assignmentId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var assignment = await ctx.CampRoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment is null) return false;
        ctx.CampRoleAssignments.Remove(assignment);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> DeleteAllForMemberAsync(Guid campMemberId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Load-then-RemoveRange so unit tests using the EF InMemory provider
        // still cover the path. ExecuteDeleteAsync would be cheaper at scale
        // but is not supported by the InMemory provider.
        var toDelete = await ctx.CampRoleAssignments
            .Where(a => a.CampMemberId == campMemberId)
            .ToListAsync(ct);
        if (toDelete.Count == 0) return 0;
        ctx.CampRoleAssignments.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetAllAssignmentsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.Definition)
            .Include(a => a.CampMember)
            .Include(a => a.CampSeason).ThenInclude(s => s.Camp)
            .Where(a => a.CampMember.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid CampSeasonId, Guid DefinitionId, int Count)>> GetAssignmentCountsForYearAsync(
        int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.CampRoleAssignments.AsNoTracking()
            .Where(a => a.CampSeason.Year == year)
            .GroupBy(a => new { a.CampSeasonId, a.CampRoleDefinitionId })
            .Select(g => new { g.Key.CampSeasonId, g.Key.CampRoleDefinitionId, Count = g.Count() })
            .ToListAsync(ct);
        return rows.Select(r => (r.CampSeasonId, r.CampRoleDefinitionId, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForDefinitionInYearAsync(
        Guid definitionId, int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Display sort happens in the service (CampRoleService.BuildDrillDownAsync).
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .Where(a => a.CampRoleDefinitionId == definitionId && a.CampSeason.Year == year)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetActiveAssignmentsForYearsAsync(
        IReadOnlyCollection<int> years, CancellationToken ct = default)
    {
        if (years.Count == 0) return [];
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var yearList = years.Distinct().ToList();
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .Include(a => a.CampSeason)
            .Include(a => a.Definition)
            .Where(a => yearList.Contains(a.CampSeason.Year)
                     && a.Definition.DeactivatedAt == null
                     && a.CampMember.Status == CampMemberStatus.Active)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Account-merge fold
    // ==========================================================================

    public async Task<int> ReassignMembershipsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Tracked — these rows are re-pointed (UserId) or deleted below.
        var sourceMembers = await ctx.CampMembers
            .Where(m => m.UserId == sourceUserId)
            .ToListAsync(ct);
        if (sourceMembers.Count == 0)
            return await ctx.CampMembers.CountAsync(m => m.UserId == targetUserId, ct);

        var sourceSeasonIds = sourceMembers.Select(m => m.CampSeasonId).Distinct().ToList();

        // Survivor's live (non-Removed) membership per affected season. A live source
        // member in one of these seasons would collide on IX_camp_members_active_unique,
        // so it is folded-then-dropped rather than re-pointed. Removed source members
        // never collide (the partial index excludes them) and always re-point.
        var targetLiveMemberIdBySeason = (await ctx.CampMembers.AsNoTracking()
                .Where(m => m.UserId == targetUserId
                    && m.Status != CampMemberStatus.Removed
                    && sourceSeasonIds.Contains(m.CampSeasonId))
                .Select(m => new { m.Id, m.CampSeasonId })
                .ToListAsync(ct))
            .ToDictionary(m => m.CampSeasonId, m => m.Id);

        // source CampMemberId -> survivor CampMemberId, for colliding seasons only.
        var collidingMemberToTarget = new Dictionary<Guid, Guid>();
        foreach (var sm in sourceMembers)
        {
            var isLive = sm.Status != CampMemberStatus.Removed;
            if (isLive && targetLiveMemberIdBySeason.TryGetValue(sm.CampSeasonId, out var targetMemberId))
            {
                collidingMemberToTarget[sm.Id] = targetMemberId;
            }
            else
            {
                // Re-point the membership; its CampRoleAssignment rows ride along on the
                // unchanged CampMemberId. UserId is init-only -> mutate via change-tracker.
                ctx.Entry(sm).Property(nameof(CampMember.UserId)).CurrentValue = targetUserId;
            }
        }

        // Fold the colliding source members' role assignments onto the survivor's member,
        // deduping against IX_camp_role_assignments_unique
        // (CampSeasonId, CampRoleDefinitionId, CampMemberId), before those members go.
        if (collidingMemberToTarget.Count > 0)
        {
            var collidingMemberIds = collidingMemberToTarget.Keys.ToList();
            var targetMemberIds = collidingMemberToTarget.Values.Distinct().ToList();

            var existingKeys = new HashSet<(Guid, Guid, Guid)>(
                (await ctx.CampRoleAssignments.AsNoTracking()
                    .Where(a => targetMemberIds.Contains(a.CampMemberId))
                    .Select(a => new { a.CampSeasonId, a.CampRoleDefinitionId, a.CampMemberId })
                    .ToListAsync(ct))
                .Select(t => (t.CampSeasonId, t.CampRoleDefinitionId, t.CampMemberId)));

            var sourceAssignments = await ctx.CampRoleAssignments
                .Where(a => collidingMemberIds.Contains(a.CampMemberId))
                .ToListAsync(ct);

            foreach (var src in sourceAssignments)
            {
                var targetMemberId = collidingMemberToTarget[src.CampMemberId];
                var key = (src.CampSeasonId, src.CampRoleDefinitionId, targetMemberId);
                if (existingKeys.Contains(key))
                {
                    // Survivor already holds this role for the season — drop source's duplicate.
                    ctx.CampRoleAssignments.Remove(src);
                }
                else
                {
                    ctx.Entry(src).Property(nameof(CampRoleAssignment.CampMemberId))
                        .CurrentValue = targetMemberId;
                    existingKeys.Add(key);
                }
            }
        }

        // Phase 1: commit re-points + role moves/removals BEFORE deleting the colliding
        // source members, so the CampRoleAssignment -> CampMember cascade can't take a
        // just-moved role down with its old (source) member.
        await ctx.SaveChangesAsync(ct);

        if (collidingMemberToTarget.Count > 0)
        {
            ctx.CampMembers.RemoveRange(
                sourceMembers.Where(m => collidingMemberToTarget.ContainsKey(m.Id)));
            await ctx.SaveChangesAsync(ct);
        }

        return await ctx.CampMembers.CountAsync(m => m.UserId == targetUserId, ct);
    }
}
