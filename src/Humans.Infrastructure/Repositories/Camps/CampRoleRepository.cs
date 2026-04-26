using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.Camps;

public sealed class CampRoleRepository : ICampRoleRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public CampRoleRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

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
            .OrderByDescending(a => a.AssignedAt)
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
}
