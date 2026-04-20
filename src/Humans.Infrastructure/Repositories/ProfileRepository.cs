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
/// </summary>
public sealed class ProfileRepository : IProfileRepository
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;

    public ProfileRepository(HumansDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default) =>
        _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.VolunteerHistory)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public async Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, Profile>();

        var list = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync(ct);

        return list.ToDictionary(p => p.UserId);
    }

    public async Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default) =>
        await _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.VolunteerHistory)
            .ToListAsync(ct);

    public async Task<(byte[]? Data, string? ContentType)> GetProfilePictureDataAsync(
        Guid profileId, CancellationToken ct = default)
    {
        var data = await _dbContext.Profiles
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

        return await _dbContext.Profiles
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
        var colaboradorCount = await _dbContext.Profiles
            .CountAsync(p => p.MembershipTier == MembershipTier.Colaborador && !p.IsSuspended, ct);
        var asociadoCount = await _dbContext.Profiles
            .CountAsync(p => p.MembershipTier == MembershipTier.Asociado && !p.IsSuspended, ct);

        return (colaboradorCount, asociadoCount);
    }

    public async Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
        Guid profileId, CancellationToken ct = default) =>
        await _dbContext.ProfileLanguages
            .AsNoTracking()
            .Where(pl => pl.ProfileId == profileId)
            .OrderByDescending(pl => pl.Proficiency)
            .ThenBy(pl => pl.LanguageCode)
            .ToListAsync(ct);

    public async Task ReplaceLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default)
    {
        var existing = await _dbContext.ProfileLanguages
            .Where(pl => pl.ProfileId == profileId)
            .ToListAsync(ct);
        _dbContext.ProfileLanguages.RemoveRange(existing);

        if (languages.Count > 0)
            _dbContext.ProfileLanguages.AddRange(languages);

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task AddAsync(Profile profile, CancellationToken ct = default)
    {
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync(ct);
    }

    public Task UpdateAsync(CancellationToken ct = default) =>
        _dbContext.SaveChangesAsync(ct);

    public async Task ReconcileCVEntriesAsync(
        Guid profileId,
        IReadOnlyList<CVEntry> entries,
        CancellationToken ct = default)
    {
        // Load tracked entities so the change tracker can detect in-place mutations.
        var existing = await _dbContext.VolunteerHistoryEntries
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
            _dbContext.VolunteerHistoryEntries.RemoveRange(extraDuplicates);

        var existingLookup = groups.ToDictionary(g => g.Key, g => g.First());
        var incomingKeys = entries.Select(e => (e.Date, e.EventName)).ToHashSet();
        var now = _clock.GetCurrentInstant();

        // Remove entries not present in the incoming set
        var toRemove = existingLookup
            .Where(kvp => !incomingKeys.Contains(kvp.Key))
            .Select(kvp => kvp.Value)
            .ToList();
        if (toRemove.Count > 0)
            _dbContext.VolunteerHistoryEntries.RemoveRange(toRemove);

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
                _dbContext.VolunteerHistoryEntries.Add(new VolunteerHistoryEntry
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

        await _dbContext.SaveChangesAsync(ct);
    }
}
