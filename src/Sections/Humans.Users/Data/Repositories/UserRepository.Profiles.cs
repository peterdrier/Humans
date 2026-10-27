using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Users.Contracts;

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
        return await ctx.Profiles
            .AsNoTracking()
            .Include(p => p.VolunteerHistory)
            .Include(p => p.Languages)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<Profile>> GetAllProfilesAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Include(p => p.VolunteerHistory)
            .Include(p => p.Languages)
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
        Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ProfileLanguages
            .AsNoTracking()
            .Where(pl => pl.ProfileId == profileId)
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
        await UpdateUserStateFromProfileAsync(ctx, profile, ct);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(
        Profile profile, CancellationToken ct = default, IReadOnlyList<CVEntry>? volunteerHistory = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Not ctx.Profiles.Update(profile): that cascades to the navigation collections and
        // could delete related rows that are simply unloaded on this entity.
        ctx.Attach(profile);
        ctx.Entry(profile).State = EntityState.Modified;
        if (volunteerHistory is not null)
            await ReconcileCVEntriesAsync(ctx, profile.Id, volunteerHistory, ct);
        await UpdateUserStateFromProfileAsync(ctx, profile, ct);
        await ctx.SaveChangesAsync(ct);
    }

    private static async Task UpdateUserStateFromProfileAsync(
        UsersDbContext ctx, Profile profile, CancellationToken ct)
    {
        var user = await ctx.Users.FindAsync([profile.UserId], ct);
        if (user is null)
            return;

        // The names belong to the human; every Profile write funnels through here, so this is the one seam.
        CopyNamesToUser(user, profile);
        user.State = UserStateEvaluator.Classify(user, profile);
    }

    /// <summary>Mirrors the Profile's names onto the User row inside the same save.</summary>
    private static void CopyNamesToUser(User user, Profile profile)
    {
        user.BurnerName = profile.BurnerName;
        user.FirstName = profile.FirstName;
        user.LastName = profile.LastName;
    }

    /// <summary>
    /// Name mirror for the ctx-direct anonymize/merge paths, which do not route through
    /// <see cref="UpdateAsync"/>.
    /// </summary>
    private static async Task MirrorProfileNamesToUserAsync(
        UsersDbContext ctx, Profile profile, CancellationToken ct)
    {
        var user = await ctx.Users.FindAsync([profile.UserId], ct);
        if (user is not null)
            CopyNamesToUser(user, profile);
    }

    public async Task<int> EraseProfileExtrasForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is not null)
        {
            // GDPR Art. 9 health and diet data: deletion-only, because a merge must not drop it.
            profile.DietaryPreference = null;
            profile.Allergies = [];
            profile.Intolerances = [];
            profile.AllergyOtherText = null;
            profile.IntoleranceOtherText = null;
            profile.MedicalConditions = null;
            profile.ConsentCheckNotes = null;
            profile.RejectionReason = null;

            // Deletion-only too: the merge fold keeps the source IBAN so the survivor can still be paid.
            profile.Iban = null;

            var languages = await ctx.ProfileLanguages
                .Where(pl => pl.ProfileId == profile.Id)
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

    public async Task<(Guid ProfileId, string? ContentType, string? StorageKey)?> ReassignSubAggregatesToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var sourceProfile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == sourceUserId, ct);
        if (sourceProfile is null)
            return null;

        var targetProfile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == targetUserId, ct);
        if (targetProfile is null)
            return null;

        var previousPicture = (ProfileId: sourceProfile.Id,
            ContentType: sourceProfile.ProfilePictureContentType,
            StorageKey: sourceProfile.ProfilePictureStorageKey);

        var sourceVolunteerHistory = await ctx.VolunteerHistoryEntries
            .Where(v => v.ProfileId == sourceProfile.Id)
            .ToListAsync(ct);
        var targetVolunteerHistory = await ctx.VolunteerHistoryEntries
            .Where(v => v.ProfileId == targetProfile.Id)
            .ToListAsync(ct);

        var sourceLanguages = await ctx.ProfileLanguages
            .Where(l => l.ProfileId == sourceProfile.Id)
            .ToListAsync(ct);
        var targetLanguages = await ctx.ProfileLanguages
            .Where(l => l.ProfileId == targetProfile.Id)
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
                ctx.Entry(src).Property(nameof(VolunteerHistoryEntry.ProfileId)).CurrentValue = targetProfile.Id;
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
                ctx.Entry(src).Property(nameof(ProfileLanguage.ProfileId)).CurrentValue = targetProfile.Id;
                targetLanguageByCode[src.LanguageCode] = src;
            }
        }

        // The row stays as the tombstone counterpart to User.MergedToUserId; only identifying
        // scalars are cleared.
        sourceProfile.FirstName = "Merged";
        sourceProfile.LastName = "User";
        sourceProfile.BurnerName = "Merged User";
        // Clear the User-side names in the same save, or the resolver (User.BurnerName) would keep
        // rendering the merged human's real name.
        await MirrorProfileNamesToUserAsync(ctx, sourceProfile, ct);
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
        return previousPicture;
    }

    public async Task<bool> AnonymizeForDeletionByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
            return false;

        profile.FirstName = "Deleted";
        profile.LastName = "User";
        profile.BurnerName = string.Empty;
        await MirrorProfileNamesToUserAsync(ctx, profile, ct);
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

    private async Task ReconcileCVEntriesAsync(
        UsersDbContext ctx,
        Guid profileId,
        IReadOnlyList<CVEntry> entries,
        CancellationToken ct)
    {
        // Tracked, so the change tracker sees in-place mutations.
        var existing = await ctx.VolunteerHistoryEntries
            .Where(v => v.ProfileId == profileId)
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
                    ProfileId = profileId,
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
