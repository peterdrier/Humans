using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IContactFieldRepository"/>. The only
/// non-test file that touches <c>DbContext.ContactFields</c> after the Profile
/// migration lands.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class ContactFieldRepository : IContactFieldRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public ContactFieldRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<ContactField>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId)
            .OrderBy(cf => cf.DisplayOrder)
            .ThenBy(cf => cf.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContactField>> GetVisibleByProfileIdAsync(
        Guid profileId, IReadOnlyList<ContactFieldVisibility> allowedVisibilities,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId && allowedVisibilities.Contains(cf.Visibility))
            .OrderBy(cf => cf.DisplayOrder)
            .ThenBy(cf => cf.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContactField>> GetByProfileIdForMutationAsync(
        Guid profileId, CancellationToken ct = default)
    {
        // With IDbContextFactory the context is short-lived, so returned entities
        // are detached. Callers must pass mutated entities explicitly to BatchSaveAsync.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId)
            .ToListAsync(ct);
    }

    public async Task BatchSaveAsync(
        IReadOnlyList<ContactField> toAdd,
        IReadOnlyList<ContactField> toUpdate,
        IReadOnlyList<ContactField> toRemove,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        if (toRemove.Count > 0)
            ctx.ContactFields.RemoveRange(toRemove);
        if (toUpdate.Count > 0)
            ctx.ContactFields.UpdateRange(toUpdate);
        if (toAdd.Count > 0)
            ctx.ContactFields.AddRange(toAdd);
        await ctx.SaveChangesAsync(ct);
    }
}
