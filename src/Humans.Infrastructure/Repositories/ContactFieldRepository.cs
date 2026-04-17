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
/// </summary>
public sealed class ContactFieldRepository : IContactFieldRepository
{
    private readonly HumansDbContext _dbContext;

    public ContactFieldRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ContactField>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default) =>
        await _dbContext.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId)
            .OrderBy(cf => cf.DisplayOrder)
            .ThenBy(cf => cf.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ContactField>> GetVisibleByProfileIdAsync(
        Guid profileId, IReadOnlyList<ContactFieldVisibility> allowedVisibilities,
        CancellationToken ct = default) =>
        await _dbContext.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId && allowedVisibilities.Contains(cf.Visibility))
            .OrderBy(cf => cf.DisplayOrder)
            .ThenBy(cf => cf.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ContactField>> GetByProfileIdTrackedAsync(
        Guid profileId, CancellationToken ct = default) =>
        await _dbContext.ContactFields
            .Where(cf => cf.ProfileId == profileId)
            .ToListAsync(ct);

    public async Task BatchSaveAsync(
        IReadOnlyList<ContactField> toAdd,
        IReadOnlyList<ContactField> toRemove,
        CancellationToken ct = default)
    {
        _dbContext.ContactFields.RemoveRange(toRemove);
        _dbContext.ContactFields.AddRange(toAdd);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task DeleteAllForProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        // Load-then-RemoveRange for EF InMemory provider compatibility (used in unit tests).
        var fields = await _dbContext.ContactFields
            .Where(cf => cf.ProfileId == profileId)
            .ToListAsync(ct);
        _dbContext.ContactFields.RemoveRange(fields);
        await _dbContext.SaveChangesAsync(ct);
    }
}
