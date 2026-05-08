using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.Containers;

public sealed class ContainerRepository : IContainerRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public ContainerRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Container>> GetBySeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Containers
            .AsNoTracking()
            .Where(c => c.CampSeasonId == campSeasonId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Container>> GetOrgByYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Containers
            .AsNoTracking()
            .Where(c => c.CampSeasonId == null && c.Year == year)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Container>> GetAllByYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Containers
            .AsNoTracking()
            .Where(c => c.Year == year)
            .ToListAsync(ct);
    }

    public async Task<Container?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Containers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Container> AddAsync(Container container, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Containers.Add(container);
        await ctx.SaveChangesAsync(ct);
        return container;
    }

    public async Task<Container> UpdateAsync(Container container, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Containers.Update(container);
        await ctx.SaveChangesAsync(ct);
        return container;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Containers
            .Where(c => c.Id == id)
            .ExecuteDeleteAsync(ct);
    }
}
