using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// Profile operations on <see cref="UserRepository"/>.
/// </summary>
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

    public async Task<string?> GetProfilePictureContentTypeAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Profiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => p.ProfilePictureContentType)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid ProfileId, Guid UserId, string BurnerName, string ContentType, Instant UpdatedAt)>>
        GetCustomPictureRowsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.Profiles
            .AsNoTracking()
            .Where(p => p.ProfilePictureContentType != null)
            .Select(p => new { p.Id, p.UserId, p.BurnerName, p.ProfilePictureContentType, p.UpdatedAt })
            .ToListAsync(ct);

        return rows
            .Select(r => (r.Id, r.UserId, r.BurnerName, r.ProfilePictureContentType!, r.UpdatedAt))
            .ToList();
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

    public async Task UpdateAsync(Profile profile, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Attach the detached entity and mark only its own scalar properties as
        // Modified — do NOT use ctx.Profiles.Update(profile) which would cascade
        // to navigation collections (VolunteerHistory, Languages) and could delete
        // existing related rows when those collections are empty on the in-memory entity.
        ctx.Attach(profile);
        ctx.Entry(profile).State = EntityState.Modified;
        await UpdateUserStateFromProfileAsync(ctx, profile, ct);
        await ctx.SaveChangesAsync(ct);
    }

    private static async Task UpdateUserStateFromProfileAsync(
        UsersDbContext ctx, Profile profile, CancellationToken ct)
    {
        var user = await ctx.Users.FindAsync([profile.UserId], ct);
        if (user is null)
            return;

        // #1097 dual-write: the names belong to the human, not the membership profile.
        // Every Profile write funnels through AddAsync/UpdateAsync, so this is the one seam.
        CopyNamesToUser(user, profile);
        user.State = UserStateEvaluator.Classify(user, profile);
    }

    /// <summary>#1097 — mirrors the Profile's names onto the User row inside the same save.</summary>
    private static void CopyNamesToUser(User user, Profile profile)
    {
        user.BurnerName = profile.BurnerName;
        user.FirstName = profile.FirstName;
        user.LastName = profile.LastName;
    }

    /// <summary>
    /// #1097 — loads the owning User into <paramref name="ctx"/> and mirrors the profile's
    /// current names onto it. For the ctx-direct anonymize/merge paths, which do not route
    /// through <see cref="UpdateAsync"/>.
    /// </summary>
    private static async Task MirrorProfileNamesToUserAsync(
        UsersDbContext ctx, Profile profile, CancellationToken ct)
    {
        var user = await ctx.Users.FindAsync([profile.UserId], ct);
        if (user is not null)
            CopyNamesToUser(user, profile);
    }

    public Task<bool> AnonymizeForMergeByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        AnonymizeProfileInternalAsync(userId, "Merged", "User", ct);

    public Task<bool> AnonymizeForDeletionByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        AnonymizeProfileInternalAsync(userId, "Deleted", "User", ct);

    public async Task<int> EraseProfileExtrasForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is not null)
        {
            // Article 9 special-category data — health and diet. Not touched by the
            // shared anonymization path because the merge fold must not drop them.
            profile.DietaryPreference = null;
            profile.Allergies = [];
            profile.Intolerances = [];
            profile.AllergyOtherText = null;
            profile.IntoleranceOtherText = null;
            profile.MedicalConditions = null;
            profile.ConsentCheckNotes = null;
            profile.RejectionReason = null;

            // Bank details. Deletion-only for the same reason as above — the merge
            // fold keeps the source IBAN so the surviving account can still be paid.
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
            // A higher-precedence state (Rejected/Merged/Deleted) outranks Suspended, so the
            // classifier returns the row unchanged. Report only rows that actually moved — the
            // caller notifies and audits off this set.
            if (next == user.State)
                continue;

            user.State = next;
            suspended.Add(user.Id);
        }

        if (suspended.Count > 0)
            await ctx.SaveChangesAsync(ct);

        return suspended;
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

    public async Task<int> ReassignSubAggregatesToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var sourceProfile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == sourceUserId, ct);
        if (sourceProfile is null)
            return 0;

        var targetProfile = await ctx.Profiles
            .FirstOrDefaultAsync(p => p.UserId == targetUserId, ct);
        if (targetProfile is null)
            return 0;

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

        // VolunteerHistory: dedup on (year, EventName) — drop source rows with
        // a key that already exists on target, re-FK survivors. EventName
        // comparison is case-sensitive (matches today's CV reconciliation).
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

        // Languages: dedup on LanguageCode. If both have the same code, keep
        // the higher Proficiency (target wins on tie); drop the source row
        // unconditionally after potentially upgrading target's proficiency.
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

        // Anonymize the source profile in place (rolls in the work of
        // AnonymizeForMergeByUserIdAsync). The row is kept as a tombstone
        // counterpart to User.MergedToUserId; only identifying scalars are
        // cleared. ContactField rows belong to the ContactFields section
        // (IContactFieldService) and are re-FK'd by the merge orchestrator's
        // separate ContactFieldService.ReassignToUserAsync call.
        sourceProfile.FirstName = "Merged";
        sourceProfile.LastName = "User";
        // Canonical display label lives on BurnerName — set it so the tombstone shows
        // "Merged User" via the profile, not via the legacy User.DisplayName fallback.
        sourceProfile.BurnerName = "Merged User";
        // #1097 dual-write: clear the tombstone's User-side names in the same save, or the
        // resolver (User.BurnerName first) would keep rendering the merged human's real name.
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
        sourceProfile.EmergencyContactName = null;
        sourceProfile.EmergencyContactPhone = null;
        sourceProfile.EmergencyContactRelationship = null;
        sourceProfile.ContributionInterests = null;
        sourceProfile.BoardNotes = null;
        sourceProfile.UpdatedAt = updatedAt;

        await ctx.SaveChangesAsync(ct);

        var volunteerHistoryCount = await ctx.VolunteerHistoryEntries
            .CountAsync(v => v.ProfileId == targetProfile.Id, ct);
        var languageCount = await ctx.ProfileLanguages
            .CountAsync(l => l.ProfileId == targetProfile.Id, ct);

        return volunteerHistoryCount + languageCount;
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
        // #1097 dual-write — see MirrorProfileNamesToUserAsync.
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
