using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.Profiles;

/// <summary>
/// EF-backed implementation of <see cref="ICommunicationPreferenceRepository"/>.
/// The only non-test file that touches <c>DbContext.CommunicationPreferences</c>
/// after the Profile migration lands.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class CommunicationPreferenceRepository : ICommunicationPreferenceRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public CommunicationPreferenceRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<CommunicationPreference>> GetByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences
            .Where(cp => cp.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<CommunicationPreference?> GetByUserAndCategoryAsync(
        Guid userId, MessageCategory category, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences
            .FirstOrDefaultAsync(
                cp => cp.UserId == userId && cp.Category == category, ct);
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithInboxDisabledAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new HashSet<Guid>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var disabledUserIds = await ctx.CommunicationPreferences
            .Where(cp => userIds.Contains(cp.UserId) && cp.Category == category && !cp.InboxEnabled)
            .Select(cp => cp.UserId)
            .ToListAsync(ct);

        return disabledUserIds.ToHashSet();
    }

    public async Task<bool> HasAnyAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences
            .AnyAsync(cp => cp.UserId == userId, ct);
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithAnyPreferencesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new HashSet<Guid>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var usersWithPrefs = await ctx.CommunicationPreferences
            .Where(cp => userIds.Contains(cp.UserId))
            .Select(cp => cp.UserId)
            .Distinct()
            .ToListAsync(ct);

        return usersWithPrefs.ToHashSet();
    }

    public async Task<IReadOnlyList<CommunicationPreference>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences
            .AsNoTracking()
            .Where(cp => cp.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task AddAsync(CommunicationPreference preference, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CommunicationPreferences.Add(preference);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IReadOnlyList<CommunicationPreference> preferences,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CommunicationPreferences.AddRange(preferences);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<List<CommunicationPreference>> AddDefaultsOrReloadAsync(
        Guid userId, IReadOnlyList<CommunicationPreference> defaults, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        try
        {
            ctx.CommunicationPreferences.AddRange(defaults);
            await ctx.SaveChangesAsync(ct);
            return defaults.ToList();
        }
        catch (DbUpdateException)
        {
            // Another request already created the defaults — reload
            ctx.ChangeTracker.Clear();
            return await ctx.CommunicationPreferences
                .Where(cp => cp.UserId == userId)
                .ToListAsync(ct);
        }
    }

    public async Task UpdateAsync(CommunicationPreference preference, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Attach(preference);
        ctx.Entry(preference).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }
}
