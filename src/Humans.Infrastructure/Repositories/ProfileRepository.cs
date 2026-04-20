using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IProfileRepository"/>. The only
/// non-test file that touches <c>DbContext.Profiles</c> or
/// <c>DbContext.ProfileLanguages</c> after the Profile migration lands.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class ProfileRepository : IProfileRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly IClock _clock;

    public ProfileRepository(IDbContextFactory<HumansDbContext> factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Include(p => p.VolunteerHistory)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, Profile>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var list = await ctx.Profiles
            .AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync(ct);

        return list.ToDictionary(p => p.UserId);
    }

    public async Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Include(p => p.VolunteerHistory)
            .ToListAsync(ct);
    }

    public async Task<(byte[]? Data, string? ContentType)> GetProfilePictureDataAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var data = await ctx.Profiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => new { p.ProfilePictureData, p.ProfilePictureContentType })
            .FirstOrDefaultAsync(ct);

        return (data?.ProfilePictureData, data?.ProfilePictureContentType);
    }

    public async Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
            return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Where(p => userIdList.Contains(p.UserId) && p.ProfilePictureData != null)
            .Select(p => new { p.Id, p.UserId, p.UpdatedAt })
            .AsAsyncEnumerable()
            .Select(p => (p.Id, p.UserId, p.UpdatedAt.ToUnixTimeTicks()))
            .ToListAsync(ct);
    }

    public async Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var colaboradorCount = await ctx.Profiles
            .CountAsync(p => p.MembershipTier == MembershipTier.Colaborador && !p.IsSuspended, ct);
        var asociadoCount = await ctx.Profiles
            .CountAsync(p => p.MembershipTier == MembershipTier.Asociado && !p.IsSuspended, ct);

        return (colaboradorCount, asociadoCount);
    }

    public async Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ProfileLanguages
            .AsNoTracking()
            .Where(pl => pl.ProfileId == profileId)
            .OrderByDescending(pl => pl.Proficiency)
            .ThenBy(pl => pl.LanguageCode)
            .ToListAsync(ct);
    }

    public async Task ReplaceLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.ProfileLanguages
            .Where(pl => pl.ProfileId == profileId)
            .ToListAsync(ct);
        ctx.ProfileLanguages.RemoveRange(existing);

        if (languages.Count > 0)
            ctx.ProfileLanguages.AddRange(languages);

        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddAsync(Profile profile, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Profiles.Add(profile);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Profile profile, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Attach the detached entity and mark only its own scalar properties as
        // Modified — do NOT use ctx.Profiles.Update(profile) which would cascade
        // to navigation collections (VolunteerHistory, Languages) and could delete
        // existing related rows when those collections are empty on the in-memory entity.
        ctx.Attach(profile);
        ctx.Entry(profile).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReconcileCVEntriesAsync(
        Guid profileId,
        IReadOnlyList<CVEntry> entries,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Load tracked entities so the change tracker can detect in-place mutations.
        var existing = await ctx.VolunteerHistoryEntries
            .Where(v => v.ProfileId == profileId)
            .ToListAsync(ct);

        // Dedup existing rows by (Date, EventName) — first row per group wins.
        // Extra duplicates from pre-Phase-10 Guid-keyed writes are removed as
        // part of this reconcile. See Phase 10 plan: this is a one-time cleanup
        // consequence of the key switch; CVEntry has no Id field and the UI no
        // longer posts one.
        //
        // NOTE: This differs from IVolunteerHistoryService.SaveAsync which keys
        // by client-supplied Guid. CVEntry is a read projection without an Id,
        // so (Date, EventName) is the natural identity key here.
        var groups = existing.GroupBy(v => (v.Date, v.EventName)).ToList();
        var extraDuplicates = groups.SelectMany(g => g.Skip(1)).ToList();
        if (extraDuplicates.Count > 0)
            ctx.VolunteerHistoryEntries.RemoveRange(extraDuplicates);

        var existingLookup = groups.ToDictionary(g => g.Key, g => g.First());
        var incomingKeys = entries.Select(e => (e.Date, e.EventName)).ToHashSet();
        var now = _clock.GetCurrentInstant();

        // Remove entries not present in the incoming set
        var toRemove = existingLookup
            .Where(kvp => !incomingKeys.Contains(kvp.Key))
            .Select(kvp => kvp.Value)
            .ToList();
        if (toRemove.Count > 0)
            ctx.VolunteerHistoryEntries.RemoveRange(toRemove);

        // Update matched, add new
        foreach (var entry in entries)
        {
            if (existingLookup.TryGetValue((entry.Date, entry.EventName), out var match))
            {
                // Only touch UpdatedAt when the description actually changed.
                if (!string.Equals(match.Description, entry.Description, StringComparison.Ordinal))
                {
                    match.Description = entry.Description;
                    match.UpdatedAt = now;
                }
            }
            else
            {
                ctx.VolunteerHistoryEntries.Add(new VolunteerHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    Date = entry.Date,
                    EventName = entry.EventName,
                    Description = entry.Description,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
