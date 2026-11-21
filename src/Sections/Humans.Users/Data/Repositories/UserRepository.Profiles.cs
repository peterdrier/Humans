using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Users.Contracts;
using Humans.Users.Services;

namespace Humans.Users.Data.Repositories;

internal sealed partial class UserRepository
{
    public async Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
            return null;

        AttachChildren(
            [profile],
            await ctx.VolunteerHistoryEntries.AsNoTracking().Where(v => v.UserId == userId).ToListAsync(ct),
            await ctx.ProfileLanguages.AsNoTracking().Where(l => l.UserId == userId).ToListAsync(ct));
        return profile;
    }

    public async Task<IReadOnlyList<Profile>> GetAllProfilesAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profiles = await ctx.Profiles.AsNoTracking().ToListAsync(ct);
        AttachChildren(
            profiles,
            await ctx.VolunteerHistoryEntries.AsNoTracking().ToListAsync(ct),
            await ctx.ProfileLanguages.AsNoTracking().ToListAsync(ct));
        return profiles;
    }

    /// <summary>Volunteer history and languages hang off the user id, so they are joined here, not by EF.</summary>
    private static void AttachChildren(
        IReadOnlyList<Profile> profiles,
        IReadOnlyList<VolunteerHistoryEntry> history,
        IReadOnlyList<ProfileLanguage> languages)
    {
        var byUser = profiles.ToDictionary(p => p.UserId);
        foreach (var entry in history)
        {
            if (entry.UserId is { } userId && byUser.TryGetValue(userId, out var profile))
                profile.VolunteerHistory.Add(entry);
        }

        foreach (var language in languages)
        {
            if (language.UserId is { } userId && byUser.TryGetValue(userId, out var profile))
                profile.Languages.Add(language);
        }
    }

    public async Task<(string ContentType, string? StorageKey)?> GetProfilePictureLocationAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.Profiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => new { p.ProfilePictureContentType, p.ProfilePictureStorageKey })
            .FirstOrDefaultAsync(ct);
        return row?.ProfilePictureContentType is { } contentType
            ? (contentType, row.ProfilePictureStorageKey)
            : null;
    }

    public async Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ProfileLanguages
            .AsNoTracking()
            .Where(pl => pl.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task ReplaceLanguagesAsync(Guid userId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.ProfileLanguages
            .Where(pl => pl.UserId == userId)
            .ToListAsync(ct);
        ctx.ProfileLanguages.RemoveRange(existing);
        ctx.ProfileLanguages.AddRange(languages);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddAsync(Profile profile, CancellationToken ct = default, UserNames? names = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Profiles.Add(profile);
        await UpdateUserFromProfileAsync(ctx, profile, names, ct);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(
        Profile profile, CancellationToken ct = default, IReadOnlyList<CVEntry>? volunteerHistory = null,
        UserNames? names = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Not ctx.Profiles.Update(profile): that cascades to the navigation collections and
        // could delete related rows that are simply unloaded on this entity.
        ctx.Attach(profile);
        ctx.Entry(profile).State = EntityState.Modified;
        if (volunteerHistory is not null)
            await ReconcileCVEntriesAsync(ctx, profile, volunteerHistory, ct);
        await UpdateUserFromProfileAsync(ctx, profile, names, ct);
        await ctx.SaveChangesAsync(ct);
    }

    private static async Task UpdateUserFromProfileAsync(
        UsersDbContext ctx, Profile profile, UserNames? names, CancellationToken ct)
    {
        var user = await ctx.Users.FindAsync([profile.UserId], ct);
        if (user is null)
            return;

        if (names is not null)
            SetNames(user, names);
        user.State = UserStateEvaluator.Classify(user, profile);
    }

    private static void SetNames(User user, UserNames names)
    {
        user.BurnerName = names.BurnerName;
        user.FirstName = names.FirstName;
        user.LastName = names.LastName;
    }

    /// <summary>Replaces a tombstoned human's names on the users row.</summary>
    private static async Task SetTombstoneNamesAsync(
        UsersDbContext ctx, Profile profile, UserNames names, CancellationToken ct)
    {
        var user = await ctx.Users.FindAsync([profile.UserId], ct);
        if (user is not null)
            SetNames(user, names);
    }

    public async Task<int> EraseProfileExtrasForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is not null)
        {
            // GDPR Art. 9 health data: deletion-only, because a merge must not drop it.
            profile.MedicalConditions = null;

            var languages = await ctx.ProfileLanguages
                .Where(pl => pl.UserId == userId)
                .ToListAsync(ct);
            ctx.ProfileLanguages.RemoveRange(languages);
        }

        return await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> SuspendManyAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new HashSet<Guid>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var userIdList = userIds is IList<Guid> list ? list : userIds.ToList();

        // Profileless users are skipped: consent suspension only applies to onboarded humans.
        var profiles = await ctx.Profiles
            .AsNoTracking()
            .Where(p => userIdList.Contains(p.UserId))
            .ToListAsync(ct);
        var profilesByUser = profiles.ToDictionary(p => p.UserId);

        var users = await ctx.Users
            .Where(u => userIdList.Contains(u.Id)
                && u.State != UserState.Suspended
                && u.State != UserState.AdminSuspended)
            .ToListAsync(ct);

        var suspended = new HashSet<Guid>();
        foreach (var user in users)
        {
            if (!profilesByUser.TryGetValue(user.Id, out var profile))
                continue;

            var next = UserStateEvaluator.Classify(
                user, profile, isSuspended: true, isAdminSuspended: false);
            // A higher-precedence state outranks Suspended and comes back unchanged. Report only
            // rows that moved: the caller notifies and audits off this set.
            if (next == user.State)
                continue;

            user.State = next;
            suspended.Add(user.Id);
        }

        if (suspended.Count > 0)
            await ctx.SaveChangesAsync(ct);

        return suspended;
    }

    public async Task ReassignSubAggregatesToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt, Profile? keptProfile, UserNames? keptNames,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var sourceProfile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == sourceUserId, ct);
        if (sourceProfile is null)
            return;

        var targetProfile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == targetUserId, ct);
        if (targetProfile is null)
            return;

        var sourceVolunteerHistory = await ctx.VolunteerHistoryEntries
            .Where(v => v.UserId == sourceUserId)
            .ToListAsync(ct);
        var targetVolunteerHistory = await ctx.VolunteerHistoryEntries
            .Where(v => v.UserId == targetUserId)
            .ToListAsync(ct);

        var sourceLanguages = await ctx.ProfileLanguages
            .Where(l => l.UserId == sourceUserId)
            .ToListAsync(ct);
        var targetLanguages = await ctx.ProfileLanguages
            .Where(l => l.UserId == targetUserId)
            .ToListAsync(ct);

        // EventName comparison is case-sensitive, matching CV reconciliation.
        var targetVolunteerKeys = new HashSet<(int Year, string EventName)>(
            targetVolunteerHistory.Select(v => (v.Date.Year, v.EventName)));
        foreach (var src in sourceVolunteerHistory)
        {
            var key = (src.Date.Year, src.EventName);
            if (targetVolunteerKeys.Contains(key))
            {
                ctx.VolunteerHistoryEntries.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                src.UpdatedAt = updatedAt;
                targetVolunteerKeys.Add(key);
            }
        }

        var targetLanguageByCode = targetLanguages
            .ToDictionary(l => l.LanguageCode, StringComparer.OrdinalIgnoreCase);
        foreach (var src in sourceLanguages)
        {
            if (targetLanguageByCode.TryGetValue(src.LanguageCode, out var tgt))
            {
                if (src.Proficiency > tgt.Proficiency)
                {
                    tgt.Proficiency = src.Proficiency;
                }
                ctx.ProfileLanguages.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                targetLanguageByCode[src.LanguageCode] = src;
            }
        }

        if (keptProfile is not null)
        {
            ctx.Entry(targetProfile).CurrentValues.SetValues(keptProfile);
            if (keptNames is not null)
                await UpdateUserFromProfileAsync(ctx, targetProfile, keptNames, ct);
        }

        // The row stays as the tombstone counterpart to User.MergedToUserId; only identifying
        // scalars are cleared.
        // Same save as the rest of the tombstone, or the resolver (User.BurnerName) would keep
        // rendering the merged human's real name.
        await SetTombstoneNamesAsync(ctx, sourceProfile, new UserNames("Merged User", "Merged", "User"), ct);
        sourceProfile.Bio = null;
        sourceProfile.City = null;
        sourceProfile.CountryCode = null;
        sourceProfile.Latitude = null;
        sourceProfile.Longitude = null;
        sourceProfile.PlaceId = null;
        sourceProfile.AdminNotes = null;
        sourceProfile.Pronouns = null;
        sourceProfile.DateOfBirth = null;
        sourceProfile.ProfilePictureContentType = null;
        sourceProfile.ProfilePictureStorageKey = null;
        sourceProfile.EmergencyContactName = null;
        sourceProfile.EmergencyContactPhone = null;
        sourceProfile.EmergencyContactRelationship = null;
        sourceProfile.ContributionInterests = null;
        sourceProfile.BoardNotes = null;
        sourceProfile.UpdatedAt = updatedAt;

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> AnonymizeForDeletionByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
            return false;

        await SetTombstoneNamesAsync(ctx, profile, new UserNames(string.Empty, "Deleted", "User"), ct);
        profile.Bio = null;
        profile.City = null;
        profile.CountryCode = null;
        profile.Latitude = null;
        profile.Longitude = null;
        profile.PlaceId = null;
        profile.AdminNotes = null;
        profile.Pronouns = null;
        profile.DateOfBirth = null;
        profile.ProfilePictureContentType = null;
        profile.ProfilePictureStorageKey = null;
        profile.EmergencyContactName = null;
        profile.EmergencyContactPhone = null;
        profile.EmergencyContactRelationship = null;
        profile.ContributionInterests = null;
        profile.BoardNotes = null;

        var contactFields = await ctx.ContactFields
            .Where(cf => cf.UserId == userId)
            .ToListAsync(ct);
        ctx.ContactFields.RemoveRange(contactFields);

        var volunteerHistory = await ctx.VolunteerHistoryEntries
            .Where(vh => vh.UserId == userId)
            .ToListAsync(ct);
        ctx.VolunteerHistoryEntries.RemoveRange(volunteerHistory);

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    private async Task ReconcileCVEntriesAsync(
        UsersDbContext ctx,
        Profile profile,
        IReadOnlyList<CVEntry> entries,
        CancellationToken ct)
    {
        // Tracked, so the change tracker sees in-place mutations.
        var existing = await ctx.VolunteerHistoryEntries
            .Where(v => v.UserId == profile.UserId)
            .ToListAsync(ct);

        var existingLookup = existing.ToDictionary(v => v.Id);
        var incomingIds = entries
            .Where(e => e.Id != Guid.Empty)
            .Select(e => e.Id)
            .ToHashSet();
        var now = _clock.GetCurrentInstant();

        var toRemove = existing
            .Where(v => !incomingIds.Contains(v.Id))
            .ToList();
        if (toRemove.Count > 0)
            ctx.VolunteerHistoryEntries.RemoveRange(toRemove);

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
                    UserId = profile.UserId,
                    Date = entry.Date,
                    EventName = entry.EventName,
                    Description = entry.Description,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }
    }
}
