using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Containers;

internal sealed class ContainerRepository(IDbContextFactory<HumansDbContext> factory) : IContainerRepository
{
    public async Task<IReadOnlyList<Container>> GetByCampAsync(Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Containers
            .AsNoTracking()
            .Where(c => c.CampId == campId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Container>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Containers
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<Container?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Containers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Container> AddAsync(Container container, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Containers.Add(container);
        await ctx.SaveChangesAsync(ct);
        return container;
    }

    public async Task<Container> UpdateAsync(Container container, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Containers.Update(container);
        await ctx.SaveChangesAsync(ct);
        return container;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var container = await ctx.Containers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (container is null)
        {
            return;
        }

        // No FK constraint on ContainerPlacement.ContainerId — delete placements explicitly.
        var placements = await ctx.ContainerPlacements
            .Where(p => p.ContainerId == id)
            .ToListAsync(ct);
        if (placements.Count > 0)
        {
            ctx.ContainerPlacements.RemoveRange(placements);
        }

        ctx.Containers.Remove(container);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<ContainerPlacement?> GetPlacementAsync(Guid containerId, int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ContainerPlacements
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ContainerId == containerId && p.Year == year, ct);
    }

    public async Task<IReadOnlyList<ContainerPlacement>> GetPlacementsByYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ContainerPlacements
            .AsNoTracking()
            .Where(p => p.Year == year)
            .ToListAsync(ct);
    }

    public async Task UpsertPlacementAsync(ContainerPlacement placement, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.ContainerPlacements
            .FirstOrDefaultAsync(p => p.ContainerId == placement.ContainerId && p.Year == placement.Year, ct);
        if (existing is null)
        {
            ctx.ContainerPlacements.Add(placement);
        }
        else
        {
            existing.LocationGeoJson = placement.LocationGeoJson;
            existing.PlacementNotes = placement.PlacementNotes;
            existing.PlacementImageStoragePath = placement.PlacementImageStoragePath;
            existing.PlacementImageContentType = placement.PlacementImageContentType;
            existing.PlacementImageFileName = placement.PlacementImageFileName;
            existing.UpdatedAt = placement.UpdatedAt;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<ContainerPlacement> SavePlacementGeometryAsync(
        Guid containerId, int year, string geoJson, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var containerExists = await ctx.Containers
            .AsNoTracking()
            .AnyAsync(c => c.Id == containerId, ct);
        if (!containerExists)
        {
            throw new InvalidOperationException("Container not found.");
        }

        var placement = await ctx.ContainerPlacements
            .FirstOrDefaultAsync(p => p.ContainerId == containerId && p.Year == year, ct);
        if (placement is null)
        {
            placement = new ContainerPlacement
            {
                ContainerId = containerId,
                Year = year,
                LocationGeoJson = geoJson,
                CreatedAt = now,
                UpdatedAt = now,
            };
            ctx.ContainerPlacements.Add(placement);
        }
        else
        {
            placement.LocationGeoJson = geoJson;
            placement.UpdatedAt = now;
        }
        await ctx.SaveChangesAsync(ct);
        return placement;
    }

    public async Task DeletePlacementAsync(Guid containerId, int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var placement = await ctx.ContainerPlacements
            .FirstOrDefaultAsync(p => p.ContainerId == containerId && p.Year == year, ct);
        if (placement is null) return;
        ctx.ContainerPlacements.Remove(placement);
        await ctx.SaveChangesAsync(ct);
    }
}
