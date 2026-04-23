using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.GoogleIntegration;

/// <summary>
/// EF-backed implementation of <see cref="IGoogleResourceRepository"/>. The
/// only non-test file that touches <c>DbSet&lt;GoogleResource&gt;</c> after
/// the Teams sub-task <c>#540c</c> migration lands. Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class GoogleResourceRepository : IGoogleResourceRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public GoogleResourceRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<GoogleResource?> GetByIdAsync(Guid resourceId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct);
    }

    public async Task<IReadOnlyList<GoogleResource>> GetActiveByTeamIdAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId && r.IsActive)
            .OrderBy(r => r.ProvisionedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResource>>> GetActiveByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<GoogleResource>>();
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.GoogleResources
            .AsNoTracking()
            .Where(r => teamIds.Contains(r.TeamId) && r.IsActive)
            .OrderBy(r => r.ProvisionedAt)
            .ToListAsync(ct);

        var result = new Dictionary<Guid, IReadOnlyList<GoogleResource>>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            result[teamId] = Array.Empty<GoogleResource>();
        }
        foreach (var group in rows.GroupBy(r => r.TeamId))
        {
            result[group.Key] = group.ToList();
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources
            .AsNoTracking()
            .Where(r => r.IsActive)
            .GroupBy(r => r.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<GoogleResource>> GetActiveDriveFoldersAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources
            .AsNoTracking()
            .Where(r => r.IsActive && r.ResourceType == GoogleResourceType.DriveFolder)
            .ToListAsync(ct);
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources.AsNoTracking().CountAsync(ct);
    }

    public async Task<GoogleResource?> FindActiveByGoogleIdAsync(
        Guid teamId,
        string googleId,
        GoogleResourceType type,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == teamId
                && r.GoogleId == googleId
                && r.ResourceType == type
                && r.IsActive, ct);
    }

    public async Task<GoogleResource?> FindInactiveByGoogleIdAsync(
        Guid teamId,
        string googleId,
        GoogleResourceType type,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == teamId
                && r.GoogleId == googleId
                && r.ResourceType == type
                && !r.IsActive, ct);
    }

    public async Task<GoogleResource?> FindActiveGroupByEmailAsync(
        Guid teamId,
        string normalizedGroupEmail,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == teamId
                && EF.Functions.ILike(r.GoogleId, normalizedGroupEmail)
                && r.ResourceType == GoogleResourceType.Group
                && r.IsActive, ct);
    }

    public async Task<GoogleResource?> FindInactiveGroupByCandidatesAsync(
        Guid teamId,
        string googleNumericId,
        string normalizedGroupEmail,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleResources
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == teamId
                && (r.GoogleId == googleNumericId || EF.Functions.ILike(r.GoogleId, normalizedGroupEmail))
                && r.ResourceType == GoogleResourceType.Group
                && !r.IsActive, ct);
    }

    // ==========================================================================
    // Writes
    // ==========================================================================

    public async Task AddAsync(GoogleResource resource, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.GoogleResources.Add(resource);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<GoogleResource?> ReactivateAsync(
        Guid resourceId,
        string name,
        string? url,
        Instant lastSyncedAt,
        string? newGoogleId,
        DrivePermissionLevel? newPermissionLevel,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.GoogleResources.FirstOrDefaultAsync(r => r.Id == resourceId, ct);
        if (row is null)
        {
            return null;
        }

        if (newGoogleId is not null)
        {
            row.GoogleId = newGoogleId;
        }
        row.Name = name;
        row.Url = url;
        row.LastSyncedAt = lastSyncedAt;
        row.IsActive = true;
        row.ErrorMessage = null;
        if (newPermissionLevel is { } level)
        {
            row.DrivePermissionLevel = level;
        }

        await ctx.SaveChangesAsync(ct);
        return row;
    }

    public async Task UnlinkAsync(Guid resourceId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.GoogleResources.FirstOrDefaultAsync(r => r.Id == resourceId, ct);
        if (row is null)
        {
            return;
        }

        row.IsActive = false;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdatePermissionLevelAsync(
        Guid resourceId,
        DrivePermissionLevel level,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.GoogleResources.FindAsync([resourceId], ct);
        if (row is null)
        {
            return false;
        }

        row.DrivePermissionLevel = level;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<GoogleResource?> SetRestrictInheritedAccessAsync(
        Guid resourceId,
        bool restrict,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.GoogleResources.FindAsync([resourceId], ct);
        if (row is null)
        {
            return null;
        }

        row.RestrictInheritedAccess = restrict;
        await ctx.SaveChangesAsync(ct);
        return row;
    }

    public async Task<IReadOnlyList<GoogleResource>> DeactivateByTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.GoogleResources.Where(r => r.TeamId == teamId && r.IsActive);
        if (resourceType is { } rt)
        {
            query = query.Where(r => r.ResourceType == rt);
        }

        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0)
        {
            return Array.Empty<GoogleResource>();
        }

        foreach (var row in rows)
        {
            row.IsActive = false;
        }
        await ctx.SaveChangesAsync(ct);
        return rows;
    }

    public async Task MarkSyncedAsync(Guid resourceId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.GoogleResources.FindAsync([resourceId], ct);
        if (row is null)
        {
            return;
        }

        row.LastSyncedAt = now;
        row.ErrorMessage = null;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task MarkSyncedManyAsync(
        IReadOnlyCollection<Guid> resourceIds,
        Instant now,
        CancellationToken ct = default)
    {
        if (resourceIds.Count == 0)
        {
            return;
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.GoogleResources
            .Where(r => resourceIds.Contains(r.Id))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            row.LastSyncedAt = now;
            row.ErrorMessage = null;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SetErrorMessageManyAsync(
        IReadOnlyCollection<Guid> resourceIds,
        string errorMessage,
        CancellationToken ct = default)
    {
        if (resourceIds.Count == 0)
        {
            return;
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.GoogleResources
            .Where(r => resourceIds.Contains(r.Id))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            row.ErrorMessage = errorMessage;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateNameAsync(Guid resourceId, string name, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.GoogleResources.FindAsync([resourceId], ct);
        if (row is null)
        {
            return;
        }

        row.Name = name;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeactivateAsync(Guid resourceId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.GoogleResources.FindAsync([resourceId], ct);
        if (row is null)
        {
            return;
        }

        row.IsActive = false;
        await ctx.SaveChangesAsync(ct);
    }
}
