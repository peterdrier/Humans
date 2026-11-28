using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Users.Contracts;
using Humans.Base.Helpers;

namespace Humans.Users.Data.Repositories;

internal sealed class UserRepository : IUserRepository
{
    private readonly IDbContextFactory<UsersDbContext> _factory;
    private readonly IClock _clock;

    public UserRepository(IDbContextFactory<UsersDbContext> factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public UserRepository(IDbContextFactory<UsersDbContext> factory)
        : this(factory, SystemClock.Instance)
    {
    }

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .ToListAsync(ct);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    public async Task<bool> SetPreferredLanguageAsync(
        Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.PreferredLanguage = preferredLanguage;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetLastSignInAsync(
        Guid userId, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.LastSignInAt = at;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetMarkedFakeAsync(
        Guid userId, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null || user.MarkedFakeAt is not null)
            return false;

        user.MarkedFakeAt = at;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = requestedAt;
        user.DeletionScheduledFor = scheduledFor;
        user.DeletionEligibleAfter = eligibleAfter;
        await ResyncStateInContextAsync(ctx, user, ct);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;
        await ResyncStateInContextAsync(ctx, user, ct);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        // Read-only: the classifier needs the profile's name fields.
        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        user.State = UserStateEvaluator.Classify(
            user, profile,
            isSuspended: suspended && !adminSuspension,
            isAdminSuspended: suspended && adminSuspension);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetRejectedAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        user.State = UserStateEvaluator.Classify(
            user, profile,
            isSuspended: user.State == UserState.Suspended,
            isAdminSuspended: user.State == UserState.AdminSuspended,
            isRejected: true);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // Caller owns SaveChangesAsync so this composes with other field mutations in one round-trip.
    private static async Task ResyncStateInContextAsync(
        UsersDbContext ctx, User user, CancellationToken ct)
    {
        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        user.State = UserStateEvaluator.Classify(user, profile);
    }

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([sourceUserId], ct);
        if (user is null)
            return false;

        user.MergedToUserId = targetUserId;
        user.MergedAt = now;

        user.DisplayName = "Merged User";

        // Overwrite the User-side names with the fold's tombstone labels, so the resolver
        // (User.BurnerName) cannot render the merged human's real name.
        user.BurnerName = "Merged User";
        user.FirstName = "Merged";
        user.LastName = "User";

        // Scrub the legacy Identity email/username: the address already moved to the survivor,
        // so it would be redundant PII kept forever. A unique sentinel keeps Identity uniqueness.
        var tombstoneAddress = $"merged-{sourceUserId:N}@merged.local";
        user.Email = tombstoneAddress;
        user.UserName = tombstoneAddress;
        user.NormalizedUserName = tombstoneAddress.ToUpperInvariant();
        // NormalizedEmail is a being-removed shadow column (HUM0010 forbids the C# property).
        ctx.Entry(user).Property("NormalizedEmail").CurrentValue = tombstoneAddress.ToUpperInvariant();

        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // Archived via merge, not deletion.
        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;

        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        await ResyncStateInContextAsync(ctx, user, ct);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsWithExternalLoginsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Set<IdentityUserLogin<Guid>>()
            .AsNoTracking()
            .Select(l => l.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> SetContactSourceIfNullAsync(
        Guid userId, ContactSource source, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null || user.ContactSource is not null)
            return false;

        user.ContactSource = source;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<(string, string)>>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.Set<IdentityUserLogin<Guid>>()
            .AsNoTracking()
            .Where(l => userIds.Contains(l.UserId))
            .Select(l => new { l.UserId, l.LoginProvider, l.ProviderKey })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<(string, string)>)g
                    .Select(r => (r.LoginProvider, r.ProviderKey))
                    .ToList());
    }

    public async Task ReassignLoginsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var sourceLogins = await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == sourceUserId)
            .ToListAsync(ct);

        // Two-pass Remove+Add: EF identity map keys on the composite PK
        // (LoginProvider, ProviderKey), so the intermediate SaveChanges is required.
        foreach (var login in sourceLogins)
        {
            ctx.Set<IdentityUserLogin<Guid>>().Remove(login);
        }

        if (sourceLogins.Count > 0)
        {
            await ctx.SaveChangesAsync(ct);
        }

        foreach (var login in sourceLogins)
        {
            ctx.Set<IdentityUserLogin<Guid>>().Add(new IdentityUserLogin<Guid>
            {
                LoginProvider = login.LoginProvider,
                ProviderKey = login.ProviderKey,
                ProviderDisplayName = login.ProviderDisplayName,
                UserId = targetUserId
            });
        }

        if (sourceLogins.Count > 0)
        {
            await ctx.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Where(u => u.DeletionScheduledFor != null
                && u.DeletionScheduledFor <= now
                && (u.DeletionEligibleAfter == null || u.DeletionEligibleAfter <= now))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        var originalBurnerName = user.BurnerName ?? string.Empty;

        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;

        // The resolver reads User.BurnerName only, so a blank one would render empty instead of
        // the "Deleted User" tombstone.
        user.BurnerName = UserInfo.GdprAnonymizedBurnerName;
        user.FirstName = "Deleted";
        user.LastName = "User";

        // Scrub the legacy Identity email/username too; the UserEmail rows are already gone.
        var tombstoneAddress = $"deleted-{userId:N}@deleted.local";
        user.Email = tombstoneAddress;
        user.UserName = tombstoneAddress;
        user.NormalizedUserName = tombstoneAddress.ToUpperInvariant();
        // NormalizedEmail is a being-removed shadow column (HUM0010 forbids the C# property).
        ctx.Entry(user).Property("NormalizedEmail").CurrentValue = tombstoneAddress.ToUpperInvariant();

        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // External logins go too, or the erased account could still sign in.
        var logins = await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == userId)
            .ToListAsync(ct);
        ctx.Set<IdentityUserLogin<Guid>>().RemoveRange(logins);

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;

        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        await ResyncStateInContextAsync(ctx, user, ct);
        await ctx.SaveChangesAsync(ct);
        return new ExpiredDeletionAnonymizationResult(
            originalBurnerName);
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
            if (byUser.TryGetValue(entry.UserId, out var profile))
                profile.VolunteerHistory.Add(entry);
        }

        foreach (var language in languages)
        {
            if (byUser.TryGetValue(language.UserId, out var profile))
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
        Profile profile, CancellationToken ct = default, IReadOnlyList<CvEntry>? volunteerHistory = null,
        UserNames? names = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Not ctx.Profiles.Update(profile): that cascades to the navigation collections and
        // could delete related rows that are simply unloaded on this entity.
        ctx.Attach(profile);
        ctx.Entry(profile).State = EntityState.Modified;
        if (volunteerHistory is not null)
            await ReconcileCvEntriesAsync(ctx, profile, volunteerHistory, ct);
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

    private async Task ReconcileCvEntriesAsync(
        UsersDbContext ctx,
        Profile profile,
        IReadOnlyList<CvEntry> entries,
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

    public async Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<UserEmail?> GetUserEmailForUserAsync(
        Guid emailId, Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == userId, ct);
    }

    public async Task<UserEmail?> GetUserEmailByIdAsync(Guid emailId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == emailId, ct);
    }

    public async Task<IReadOnlyList<UserEmail>> GetUserEmailsByAddressAsync(
        string normalizedEmail, string? alternateEmail, CancellationToken ct = default)
    {
        // ILIKE treats '_' and '%' as wildcards, so alex_smith@example.com would match
        // alexXsmith@example.com unescaped. Escape the pattern and use '\' as the escape.
        var escaped = EscapeLikePattern(normalizedEmail);
        var escapedAlternate = alternateEmail is null ? null : EscapeLikePattern(alternateEmail);

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = ctx.UserEmails.AsNoTracking();
        rows = escapedAlternate is null
            ? rows.Where(e => EF.Functions.ILike(e.Email, escaped, "\\"))
            : rows.Where(e => EF.Functions.ILike(e.Email, escaped, "\\") ||
                              EF.Functions.ILike(e.Email, escapedAlternate, "\\"));
        return await rows.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserEmail>> GetAllUserEmailsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsWithEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(e => userIds.Contains(e.UserId))
            .Select(e => e.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task RemoveUserEmailsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var emails = await ctx.UserEmails
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);

        if (emails.Count == 0)
            return;

        ctx.UserEmails.RemoveRange(emails);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignUserEmailsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var sourceRows = await ctx.UserEmails
            .Where(e => e.UserId == sourceUserId)
            .ToListAsync(ct);
        var targetRows = await ctx.UserEmails
            .Where(e => e.UserId == targetUserId)
            .ToListAsync(ct);

        // Lower-cased defensively: Email is normalized on write, but a legacy mixed-case row may exist.
        var targetByAddress = targetRows
            .ToDictionary(e => e.Email, StringComparer.OrdinalIgnoreCase);

        foreach (var src in sourceRows)
        {
            if (targetByAddress.TryGetValue(src.Email, out var tgt))
            {
                if (src.IsVerified)
                {
                    tgt.IsVerified = true;
                }
                tgt.UpdatedAt = updatedAt;
                ctx.UserEmails.Remove(src);
            }
            else
            {
                // The survivor keeps its notification target.
                ctx.Entry(src).Property(nameof(UserEmail.UserId)).CurrentValue = targetUserId;
                src.IsPrimary = false;
                src.UpdatedAt = updatedAt;
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> MarkUserEmailVerifiedAsync(
        Guid emailId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var email = await ctx.UserEmails.FirstOrDefaultAsync(e => e.Id == emailId, ct);
        if (email is null)
            return false;

        email.IsVerified = true;
        email.UpdatedAt = now;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveUserEmailByIdAsync(Guid emailId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var email = await ctx.UserEmails.FirstOrDefaultAsync(e => e.Id == emailId, ct);
        if (email is null)
            return false;

        ctx.UserEmails.Remove(email);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<UserEmail>> GetUserEmailsByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct = default)
    {
        if (emails.Count == 0)
            return [];

        // The email directory fits in RAM. Use the same identity comparer as the Google callers,
        // preserving exact matches before Gmail dot/plus/domain aliases without SQL casing rules.
        var exactTargets = new HashSet<string>(emails, StringComparer.OrdinalIgnoreCase);
        var canonicalTargets = new HashSet<string>(emails, GmailAliasEmailComparer.Instance);
        var exact = new List<UserEmail>();
        var aliases = new List<UserEmail>();
        foreach (var row in await GetAllUserEmailsAsync(ct))
        {
            if (exactTargets.Contains(row.Email))
                exact.Add(row);
            else if (canonicalTargets.Contains(row.Email))
                aliases.Add(row);
        }
        return [.. exact, .. aliases];
    }

    public async Task AddUserEmailAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.UserEmails.Add(email);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RemoveUserEmailAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Attach(email);
        ctx.UserEmails.Remove(email);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateUserEmailAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Attach(email);
        ctx.Entry(email).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateUserEmailsAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        foreach (var email in emails)
        {
            ctx.Attach(email);
            ctx.Entry(email).State = EntityState.Modified;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ApplyUserEmailReconcilePlanAsync(
        UserEmail? rowToDelete,
        UserEmail? rowToUpdate,
        UserEmail? rowToInsert,
        CancellationToken ct = default)
    {
        if (rowToDelete is null && rowToUpdate is null && rowToInsert is null)
            return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);

        if (rowToDelete is not null)
        {
            ctx.Attach(rowToDelete);
            ctx.UserEmails.Remove(rowToDelete);
        }

        if (rowToUpdate is not null)
        {
            ctx.Attach(rowToUpdate);
            ctx.Entry(rowToUpdate).State = EntityState.Modified;
        }

        if (rowToInsert is not null)
        {
            ctx.UserEmails.Add(rowToInsert);
        }

        try
        {
            await ctx.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException dbex)
            when (dbex.InnerException is Npgsql.PostgresException
            { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_user_emails_Email" })
        {
            // The verified-email unique index caught a concurrent cross-user insert. Translated
            // here so callers never see Npgsql types; the sign-in callers swallow and log it so
            // sign-in never blocks.
            throw new OAuthReconcileConcurrencyException(
                "OAuth reconcile race: a concurrent insert violated the " +
                "verified-email partial unique index (Postgres 23505).",
                dbex);
        }
    }

    public async Task<IReadOnlyList<ContactField>> GetAllContactFieldsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContactField>> GetContactFieldsByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ContactFields
            .AsNoTracking()
            .Where(cf => cf.UserId == userId)
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

    public async Task ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (!await ctx.Profiles.AnyAsync(p => p.UserId == targetUserId, ct))
            return;

        var sourceRows = await ctx.ContactFields
            .Where(cf => cf.UserId == sourceUserId)
            .ToListAsync(ct);
        var targetRows = await ctx.ContactFields
            .Where(cf => cf.UserId == targetUserId)
            .ToListAsync(ct);

        // Value has no canonical comparison, so match case-insensitively; target wins.
        var targetKeys = targetRows
            .Select(cf => (cf.FieldType, Value: cf.Value.ToLowerInvariant()))
            .ToHashSet();

        foreach (var src in sourceRows)
        {
            var key = (src.FieldType, Value: src.Value.ToLowerInvariant());
            if (targetKeys.Contains(key))
            {
                ctx.ContactFields.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                src.UpdatedAt = updatedAt;
                targetKeys.Add(key);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
