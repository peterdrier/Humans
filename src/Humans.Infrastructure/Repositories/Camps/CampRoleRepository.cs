using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Camps;

internal sealed class CampRoleRepository(IDbContextFactory<HumansDbContext> factory) : ICampRoleRepository
{
    public async Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var query = ctx.CampRoleDefinitions.AsNoTracking().AsQueryable();
        if (!includeDeactivated)
            query = query.Where(d => d.DeactivatedAt == null);
        return await query.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<CampRoleDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task<CampRoleDefinition?> GetDefinitionBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.SpecialRole == specialRole, ct);
    }

    public async Task<IReadOnlyList<CampSpecialRole>> GetExistingSpecialRolesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleDefinitions.AsNoTracking()
            .Where(d => d.SpecialRole != CampSpecialRole.None)
            .Select(d => d.SpecialRole)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> IsUserSpecialRoleHolderForCampAsync(
        Guid userId, Guid campId, IReadOnlyCollection<CampSpecialRole> specialRoles, CancellationToken ct = default)
    {
        if (specialRoles.Count == 0) return false;
        var roleList = specialRoles.ToList();
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .AnyAsync(a => a.CampMember.UserId == userId
                && a.CampSeason.CampId == campId
                && roleList.Contains(a.Definition.SpecialRole)
                && a.Definition.DeactivatedAt == null, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetCampIdsBySpecialRolesForUserAsync(
        Guid userId, IReadOnlyCollection<CampSpecialRole> specialRoles, CancellationToken ct = default)
    {
        if (specialRoles.Count == 0) return [];
        var roleList = specialRoles.ToList();
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Where(a => a.CampMember.UserId == userId
                && roleList.Contains(a.Definition.SpecialRole)
                && a.Definition.DeactivatedAt == null)
            .Select(a => a.CampSeason.CampId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<Guid?> GetCampSpecialRoleSeasonIdForYearAsync(
        Guid userId, int year, CampSpecialRole specialRole, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Where(a => a.CampMember.UserId == userId
                && a.CampSeason.Year == year
                && a.Definition.SpecialRole == specialRole
                && a.Definition.DeactivatedAt == null)
            .OrderBy(a => a.CampSeasonId) // arch:db-sort-ok — top-1 deterministic pick
            .Select(a => (Guid?)a.CampSeasonId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> CountPendingMembershipsForSpecialRoleHolderAsync(
        Guid userId, CampSpecialRole specialRole, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Camps where the user holds the given special role on any active season.
        var leadCampIds = ctx.CampRoleAssignments.AsNoTracking()
            .Where(a => a.CampMember.UserId == userId
                && a.Definition.SpecialRole == specialRole
                && a.Definition.DeactivatedAt == null)
            .Select(a => a.CampSeason.CampId)
            .Distinct();

        return await ctx.CampMembers.AsNoTracking()
            .Where(m => m.Status == CampMemberStatus.Pending
                && leadCampIds.Contains(m.CampSeason.CampId)
                && (m.CampSeason.Status == CampSeasonStatus.Active
                    || m.CampSeason.Status == CampSeasonStatus.Full))
            .CountAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetSpecialRoleHolderUserIdsAsync(
        CampSpecialRole specialRole, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .AnyAsync(a => a.CampMember.UserId == userId
                && a.Definition.SpecialRole == specialRole
                && a.Definition.DeactivatedAt == null, ct);
    }

    public async Task<bool> DefinitionSlugExistsAsync(string slug, Guid? excludingId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.CampRoleDefinitions.Add(definition);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateDefinitionAsync(Guid id, Action<CampRoleDefinition> mutate, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var def = await ctx.CampRoleDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (def is null) return false;
        mutate(def);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.Definition)
            .Include(a => a.CampMember)
            .Where(a => a.CampSeasonId == campSeasonId)
            .OrderBy(a => a.Definition.SortOrder).ThenBy(a => a.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<CampRoleAssignment?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
    }

    public async Task<int> CountAssignmentsForSeasonAndDefinitionAsync(Guid campSeasonId, Guid definitionId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .CountAsync(a => a.CampSeasonId == campSeasonId && a.CampRoleDefinitionId == definitionId, ct);
    }

    public async Task<bool> AssignmentExistsAsync(Guid campSeasonId, Guid definitionId, Guid campMemberId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .AnyAsync(a => a.CampSeasonId == campSeasonId
                        && a.CampRoleDefinitionId == definitionId
                        && a.CampMemberId == campMemberId, ct);
    }

    public async Task<bool> AddAssignmentAsync(CampRoleAssignment assignment, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var assignment = await ctx.CampRoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment is null) return false;
        ctx.CampRoleAssignments.Remove(assignment);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> DeleteAllForMemberAsync(Guid campMemberId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.Definition)
            .Include(a => a.CampMember)
            .Include(a => a.CampSeason).ThenInclude(s => s.Camp)
            .Where(a => a.CampMember.UserId == userId)
            .OrderByDescending(a => a.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid CampSeasonId, Guid DefinitionId, int Count)>> GetAssignmentCountsForYearAsync(
        int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var yearList = years.Distinct().ToList();
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .Include(a => a.CampSeason)
            .Include(a => a.Definition)
            .Where(a => yearList.Contains(a.CampSeason.Year)
                     && a.Definition.DeactivatedAt == null
                     && a.CampMember.Status == Humans.Domain.Enums.CampMemberStatus.Active)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Account-merge fold
    // ==========================================================================

    public async Task<int> ReassignAssignmentsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // CampRoleAssignment is keyed by CampMemberId, not UserId. To "move
        // by user" we walk source's CampMembers and find target's CampMember
        // in the same season (the CampMember unique-active index is on
        // (CampSeasonId, UserId), so target has at most one per season).
        // For each source assignment whose source-member's season has a
        // target-member, re-FK CampMemberId to that target-member; on
        // collision against the unique index
        // (CampSeasonId, CampRoleDefinitionId, CampMemberId) target wins
        // (drop source). Assignments whose season has no target-member are
        // left in place — source's CampMember row is the tombstone, not in
        // scope for this method.
        var sourceMembers = await ctx.CampMembers.AsNoTracking()
            .Where(m => m.UserId == sourceUserId)
            .Select(m => new { m.Id, m.CampSeasonId })
            .ToListAsync(ct);
        if (sourceMembers.Count == 0)
            return await ctx.CampRoleAssignments
                .CountAsync(a => a.CampMember.UserId == targetUserId, ct);

        var sourceMemberIds = sourceMembers.Select(m => m.Id).ToList();
        var sourceSeasonIds = sourceMembers.Select(m => m.CampSeasonId).Distinct().ToList();

        var targetMembersBySeason = await ctx.CampMembers.AsNoTracking()
            .Where(m => m.UserId == targetUserId
                && sourceSeasonIds.Contains(m.CampSeasonId))
            .Select(m => new { m.Id, m.CampSeasonId })
            .ToListAsync(ct);
        var targetMemberIdBySeason = targetMembersBySeason
            .ToDictionary(m => m.CampSeasonId, m => m.Id);

        // Map source CampMemberId -> target CampMemberId (when target has a
        // member in the same season; otherwise no entry).
        var sourceToTargetMember = new Dictionary<Guid, Guid>();
        foreach (var sm in sourceMembers)
        {
            if (targetMemberIdBySeason.TryGetValue(sm.CampSeasonId, out var targetMemberId))
                sourceToTargetMember[sm.Id] = targetMemberId;
        }

        if (sourceToTargetMember.Count == 0)
            return await ctx.CampRoleAssignments
                .CountAsync(a => a.CampMember.UserId == targetUserId, ct);

        var targetMemberIds = sourceToTargetMember.Values.Distinct().ToList();

        // Existing target assignments — used for collision detection on
        // (CampSeasonId, CampRoleDefinitionId, CampMemberId).
        var targetExisting = await ctx.CampRoleAssignments.AsNoTracking()
            .Where(a => targetMemberIds.Contains(a.CampMemberId))
            .Select(a => new { a.CampSeasonId, a.CampRoleDefinitionId, a.CampMemberId })
            .ToListAsync(ct);
        var targetExistingKeys = new HashSet<(Guid, Guid, Guid)>(
            targetExisting.Select(t => (t.CampSeasonId, t.CampRoleDefinitionId, t.CampMemberId)));

        var sourceAssignments = await ctx.CampRoleAssignments
            .Where(a => sourceMemberIds.Contains(a.CampMemberId))
            .ToListAsync(ct);

        foreach (var src in sourceAssignments)
        {
            if (!sourceToTargetMember.TryGetValue(src.CampMemberId, out var targetMemberId))
            {
                // Target has no CampMember in this season — leave source's
                // assignment in place.
                continue;
            }

            var collisionKey = (src.CampSeasonId, src.CampRoleDefinitionId, targetMemberId);
            if (targetExistingKeys.Contains(collisionKey))
            {
                // Target already holds this role for this season — target
                // wins, drop source's row.
                ctx.CampRoleAssignments.Remove(src);
            }
            else
            {
                // Re-FK to target's CampMember. CampMemberId is init-only;
                // mutate via the EF change-tracker.
                ctx.Entry(src).Property(nameof(CampRoleAssignment.CampMemberId))
                    .CurrentValue = targetMemberId;
                // Track the new key so a second source row that would
                // resolve to the same target slot is also de-duplicated.
                targetExistingKeys.Add(collisionKey);
            }
        }

        await ctx.SaveChangesAsync(ct);

        return await ctx.CampRoleAssignments
            .CountAsync(a => a.CampMember.UserId == targetUserId, ct);
    }
}
