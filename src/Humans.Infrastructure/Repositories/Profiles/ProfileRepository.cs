using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.Profiles;

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

    public async Task<Guid?> GetOwnerUserIdAsync(Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => (Guid?)p.UserId)
            .FirstOrDefaultAsync(ct);
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

    public async Task<IReadOnlyList<Profile>> GetReviewableAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Where(p => !p.IsApproved && p.RejectedAt == null)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<int> GetReviewableCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .CountAsync(p => !p.IsApproved && p.RejectedAt == null, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetPendingConsentCheckUserIdsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Where(p => p.ConsentCheckStatus == ConsentCheckStatus.Pending
                && !p.IsApproved
                && p.RejectedAt == null)
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.UserId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetApprovedUserIdsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Where(p => p.IsApproved && !p.IsSuspended)
            .Select(p => p.UserId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveApprovedUserIdsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Where(p => p.IsApproved && !p.IsSuspended)
            .Select(p => p.UserId)
            .ToListAsync(ct);
    }

    public async Task<int> GetConsentReviewPendingCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .CountAsync(p =>
                p.ConsentCheckStatus != null &&
                (p.ConsentCheckStatus == ConsentCheckStatus.Pending ||
                 p.ConsentCheckStatus == ConsentCheckStatus.Flagged) &&
                p.RejectedAt == null, ct);
    }

    public async Task<int> GetNotApprovedAndNotSuspendedCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .CountAsync(p => !p.IsApproved && !p.IsSuspended, ct);
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

    public Task<bool> AnonymizeForMergeByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        AnonymizeProfileInternalAsync(userId, "Merged", "User", ct);

    public Task<bool> AnonymizeForDeletionByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        AnonymizeProfileInternalAsync(userId, "Deleted", "User", ct);

    public async Task<IReadOnlySet<Guid>> SuspendManyAsync(
        IReadOnlyCollection<Guid> userIds,
        Instant now,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new HashSet<Guid>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var userIdList = userIds is IList<Guid> list ? list : userIds.ToList();
        var profiles = await ctx.Profiles
            .Where(p => userIdList.Contains(p.UserId) && !p.IsSuspended)
            .ToListAsync(ct);

        foreach (var profile in profiles)
        {
            profile.IsSuspended = true;
            profile.UpdatedAt = now;
        }

        if (profiles.Count > 0)
        {
            await ctx.SaveChangesAsync(ct);
        }

        return profiles.Select(p => p.UserId).ToHashSet();
    }

    public async Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var keepList = userIdsToKeep is IList<Guid> list ? list : userIdsToKeep.ToList();
        var profiles = await ctx.Profiles
            .Where(p => p.MembershipTier == currentTier && !keepList.Contains(p.UserId))
            .ToListAsync(ct);

        var result = new List<(Guid UserId, MembershipTier NewTier)>(profiles.Count);
        foreach (var profile in profiles)
        {
            var newTier = fallbackTierByUser.TryGetValue(profile.UserId, out var other)
                ? other
                : MembershipTier.Volunteer;
            profile.MembershipTier = newTier;
            profile.UpdatedAt = now;
            result.Add((profile.UserId, newTier));
        }

        if (profiles.Count > 0)
        {
            await ctx.SaveChangesAsync(ct);
        }

        return result;
    }

    private async Task<bool> AnonymizeProfileInternalAsync(
        Guid userId, string firstName, string lastName, CancellationToken ct)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
            return false;

        profile.FirstName = firstName;
        profile.LastName = lastName;
        profile.BurnerName = string.Empty;
        profile.Bio = null;
        profile.City = null;
        profile.CountryCode = null;
        profile.Latitude = null;
        profile.Longitude = null;
        profile.PlaceId = null;
        profile.AdminNotes = null;
        profile.Pronouns = null;
        profile.DateOfBirth = null;
        profile.ProfilePictureData = null;
        profile.ProfilePictureContentType = null;
        profile.EmergencyContactName = null;
        profile.EmergencyContactPhone = null;
        profile.EmergencyContactRelationship = null;
        profile.ContributionInterests = null;
        profile.BoardNotes = null;

        var contactFields = await ctx.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync(ct);
        ctx.ContactFields.RemoveRange(contactFields);

        var volunteerHistory = await ctx.VolunteerHistoryEntries
            .Where(vh => vh.ProfileId == profile.Id)
            .ToListAsync(ct);
        ctx.VolunteerHistoryEntries.RemoveRange(volunteerHistory);

        await ctx.SaveChangesAsync(ct);
        return true;
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

        // Reconcile keyed by Id (the stable per-row identity):
        //   - entries with an Id that matches an existing row update that row
        //     in place (keep Id/CreatedAt, bump UpdatedAt only when fields
        //     actually change);
        //   - entries with Guid.Empty or an unknown Id are inserted with a
        //     freshly generated Id;
        //   - existing rows whose Id is absent from the incoming set are deleted.
        var existingLookup = existing.ToDictionary(v => v.Id);
        var incomingIds = entries
            .Where(e => e.Id != Guid.Empty)
            .Select(e => e.Id)
            .ToHashSet();
        var now = _clock.GetCurrentInstant();

        // Remove entries whose Id is not in the incoming set
        var toRemove = existing
            .Where(v => !incomingIds.Contains(v.Id))
            .ToList();
        if (toRemove.Count > 0)
            ctx.VolunteerHistoryEntries.RemoveRange(toRemove);

        // Update matched, add new
        foreach (var entry in entries)
        {
            if (entry.Id != Guid.Empty && existingLookup.TryGetValue(entry.Id, out var match))
            {
                // Only touch UpdatedAt when a field actually changed.
                var changed =
                    match.Date != entry.Date ||
                    !string.Equals(match.EventName, entry.EventName, StringComparison.Ordinal) ||
                    !string.Equals(match.Description, entry.Description, StringComparison.Ordinal);
                if (changed)
                {
                    match.Date = entry.Date;
                    match.EventName = entry.EventName;
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
