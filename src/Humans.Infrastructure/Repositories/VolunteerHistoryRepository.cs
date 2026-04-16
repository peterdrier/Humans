using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IVolunteerHistoryRepository"/>. The only
/// non-test file that touches <c>DbContext.VolunteerHistoryEntries</c> after the
/// Profile migration lands.
/// </summary>
public sealed class VolunteerHistoryRepository : IVolunteerHistoryRepository
{
    private readonly HumansDbContext _dbContext;

    public VolunteerHistoryRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<VolunteerHistoryEntry>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default) =>
        await _dbContext.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .OrderByDescending(v => v.Date)
            .ThenByDescending(v => v.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VolunteerHistoryEntry>> GetByProfileIdTrackedAsync(
        Guid profileId, CancellationToken ct = default) =>
        await _dbContext.VolunteerHistoryEntries
            .Where(v => v.ProfileId == profileId)
            .ToListAsync(ct);

    public async Task BatchSaveAsync(
        IReadOnlyList<VolunteerHistoryEntry> toAdd,
        IReadOnlyList<VolunteerHistoryEntry> toRemove,
        CancellationToken ct = default)
    {
        _dbContext.VolunteerHistoryEntries.RemoveRange(toRemove);
        _dbContext.VolunteerHistoryEntries.AddRange(toAdd);
        // Tracked entities from GetByProfileIdTrackedAsync that were mutated
        // in-place are automatically detected by the change tracker.
        await _dbContext.SaveChangesAsync(ct);
    }
}
