using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="ICommunicationPreferenceRepository"/>.
/// The only non-test file that touches <c>DbContext.CommunicationPreferences</c>
/// after the Profile migration lands.
/// </summary>
public sealed class CommunicationPreferenceRepository : ICommunicationPreferenceRepository
{
    private readonly HumansDbContext _dbContext;

    public CommunicationPreferenceRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<List<CommunicationPreference>> GetByUserIdAsync(
        Guid userId, CancellationToken ct = default) =>
        _dbContext.CommunicationPreferences
            .Where(cp => cp.UserId == userId)
            .ToListAsync(ct);

    public Task<CommunicationPreference?> GetByUserAndCategoryAsync(
        Guid userId, MessageCategory category, CancellationToken ct = default) =>
        _dbContext.CommunicationPreferences
            .FirstOrDefaultAsync(
                cp => cp.UserId == userId && cp.Category == category, ct);

    public async Task<IReadOnlySet<Guid>> GetUsersWithInboxDisabledAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new HashSet<Guid>();

        var disabledUserIds = await _dbContext.CommunicationPreferences
            .Where(cp => userIds.Contains(cp.UserId) && cp.Category == category && !cp.InboxEnabled)
            .Select(cp => cp.UserId)
            .ToListAsync(ct);

        return disabledUserIds.ToHashSet();
    }

    public Task<bool> HasAnyAsync(Guid userId, CancellationToken ct = default) =>
        _dbContext.CommunicationPreferences
            .AnyAsync(cp => cp.UserId == userId, ct);

    public async Task<IReadOnlySet<Guid>> GetUsersWithAnyPreferencesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new HashSet<Guid>();

        var usersWithPrefs = await _dbContext.CommunicationPreferences
            .Where(cp => userIds.Contains(cp.UserId))
            .Select(cp => cp.UserId)
            .Distinct()
            .ToListAsync(ct);

        return usersWithPrefs.ToHashSet();
    }

    public async Task<IReadOnlyList<CommunicationPreference>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.CommunicationPreferences
            .AsNoTracking()
            .Where(cp => cp.UserId == userId)
            .ToListAsync(ct);

    public async Task AddAsync(CommunicationPreference preference, CancellationToken ct = default)
    {
        _dbContext.CommunicationPreferences.Add(preference);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IReadOnlyList<CommunicationPreference> preferences,
        CancellationToken ct = default)
    {
        _dbContext.CommunicationPreferences.AddRange(preferences);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<List<CommunicationPreference>> AddDefaultsOrReloadAsync(
        Guid userId, IReadOnlyList<CommunicationPreference> defaults, CancellationToken ct = default)
    {
        try
        {
            _dbContext.CommunicationPreferences.AddRange(defaults);
            await _dbContext.SaveChangesAsync(ct);
            return defaults.ToList();
        }
        catch (DbUpdateException)
        {
            // Another request already created the defaults — reload
            _dbContext.ChangeTracker.Clear();
            return await _dbContext.CommunicationPreferences
                .Where(cp => cp.UserId == userId)
                .ToListAsync(ct);
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        _dbContext.SaveChangesAsync(ct);
}
