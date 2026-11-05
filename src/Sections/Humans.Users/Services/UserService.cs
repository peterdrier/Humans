using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Onboarding.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Base.Threading;
using Humans.Base.Helpers;
using Humans.Users.Helpers;
using Humans.Gdpr.Contracts;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class UserService(
    IUserRepository repo,
    IAdminAuthorizationService adminAuthorization,
    IFileStorage fileStorage,
    IClock clock,
    ILogger<UserService> logger,
    IAuditLogService audit) : IUserManagement, IUserDataContributor
{
    internal const string Account = UserDataExport.Account;
    internal const string Profile = UserDataExport.Profile;
    internal const string MergedProfiles = UserDataExport.MergedProfiles;
    internal const string ContactFields = UserDataExport.ContactFields;
    internal const string UserEmails = UserDataExport.UserEmails;
    internal const string VolunteerHistory = UserDataExport.VolunteerHistory;
    internal const string Languages = UserDataExport.Languages;

    private readonly UserDataExport dataExport = new(repo);

    private readonly UserEmailMutations emailMutations = new(repo, clock, logger);

    private static readonly TrackedLock[] ProfileStubLocks = Enumerable
        .Range(0, 32)
        .Select(i => new TrackedLock($"UserService.ProfileStub[{i}]"))
        .ToArray();

    private static TrackedLock ProfileStubLockFor(Guid userId) =>
        ProfileStubLocks[(uint)userId.GetHashCode() % (uint)ProfileStubLocks.Length];

    /// <summary>
    /// Only <c>CachingUserService</c> sees the whole graph, so here raw and redirecting reads are
    /// the same; the redirect is tested through the decorator.
    /// </summary>
    public ValueTask<UserInfo?> GetRawUserInfoAsync(Guid userId, CancellationToken ct = default) =>
        GetUserInfoAsync(userId, ct);

    /// <inheritdoc cref="GetRawUserInfoAsync" />
    public Task<IReadOnlyCollection<UserInfo>> GetAllRawUserInfosAsync(CancellationToken ct = default) =>
        GetAllUserInfosAsync(ct);

    public async ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await repo.GetByIdAsync(userId, ct);
        if (user is null) return null;

        var userEmails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        var externalLoginsMap = await repo.GetExternalLoginsByUserIdsAsync([userId], ct);
        var externalLogins = externalLoginsMap.TryGetValue(userId, out var logins)
            ? logins
            : [];

        var profile = await repo.GetByUserIdReadOnlyAsync(userId, ct);
        IReadOnlyList<ContactField> contactFields = [];
        IReadOnlyList<ProfileLanguage> languages = [];
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = [];
        if (profile is not null)
        {
            contactFields = await repo.GetByProfileIdReadOnlyAsync(profile.Id, ct);
            languages = profile.Languages.ToList();
            volunteerHistory = profile.VolunteerHistory.ToList();
        }

        var info = UserInfoFactory.Create(
            user, userEmails, externalLogins,
            profile, contactFields, languages, volunteerHistory);

        return info;
    }

    public async Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default)
    {
        var users = await repo.GetAllAsync(ct);
        if (users.Count == 0) return [];

        var userIds = users.Select(u => u.Id).ToList();

        var allEmails = await repo.GetAllUserEmailsAsync(ct);
        var emailsByUser = allEmails
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UserEmail>)g.ToList());

        var loginsByUser = await repo.GetExternalLoginsByUserIdsAsync(userIds, ct);

        var profiles = await repo.GetAllProfilesAsync(ct);
        var profileByUser = profiles.ToDictionary(p => p.UserId);

        var allContactFields = await repo.GetAllContactFieldsAsync(ct);
        var contactFieldsByProfile = allContactFields
            .GroupBy(c => c.ProfileId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ContactField>)g.ToList());

        var result = new List<UserInfo>(users.Count);
        foreach (var user in users)
        {
            var emails = emailsByUser.TryGetValue(user.Id, out var es) ? es : [];
            var logins = loginsByUser.TryGetValue(user.Id, out var ls) ? ls : [];
            profileByUser.TryGetValue(user.Id, out var profile);
            IReadOnlyList<ContactField> contactFields = [];
            IReadOnlyList<ProfileLanguage> languages = [];
            IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = [];
            if (profile is not null)
            {
                contactFields = contactFieldsByProfile.TryGetValue(profile.Id, out var cf) ? cf : [];
                languages = profile.Languages.ToList();
                volunteerHistory = profile.VolunteerHistory.ToList();
            }

            var info = UserInfoFactory.Create(
                user, emails, logins,
                profile, contactFields, languages, volunteerHistory);
            result.Add(info);
        }

        return result;
    }

    public async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, UserInfo>();

        var requested = userIds as IReadOnlySet<Guid> ?? new HashSet<Guid>(userIds);
        var all = await GetAllUserInfosAsync(ct);
        return all
            .Where(u => requested.Contains(u.Id))
            .ToDictionary(u => u.Id);
    }

    public Task<IReadOnlyList<HumanSearchResult>> SearchUsersAsync(
        string query, PersonSearchFields fields, int limit = 10, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "SearchUsersAsync runs against the cached UserInfo snapshot in " +
            "CachingUserService. If this is being called on the inner UserService " +
            "it indicates a DI registration mistake — IUserManagement should resolve " +
            "to CachingUserService.");

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Own data only; the cross-section cascade is IAccountDeletionService's.
        if (await repo.GetByIdAsync(userId, ct) is null)
            return null;

        await repo.RemoveAllUserEmailsForUserAndSaveAsync(userId, ct);
        return await repo.ApplyExpiredDeletionAnonymizationAsync(userId, ct);
    }

    /// <summary>Verified rows only, matching the address or its gmail/googlemail alternate.</summary>
    /// <remarks>
    /// Verified-only is load-bearing: Google sync asks which account owns an address, and an
    /// unverified address must never resolve. The snapshot match is <c>CachingUserService</c>'s;
    /// repeating it here would re-derive the whole snapshot on every miss.
    /// </remarks>
    public async Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        var normalized = EmailNormalization.NormalizeForComparison(email);
        var alternate = EmailLookupAliases.GetAlternate(normalized);

        // The query needs the alternate (ILIKE matches the literal string); the in-memory re-check
        // doesn't, since EmailsMatch folds gmail/googlemail on both sides.
        var rows = await repo.GetUserEmailsByAddressAsync(normalized, alternate, ct);

        var match = rows
            .Where(r => r.IsVerified && EmailNormalization.EmailsMatch(r.Email, normalized))
            .OrderByDescending(r => r.IsPrimary)
            .FirstOrDefault();

        return match is null ? null : await GetUserInfoAsync(match.UserId, ct);
    }

    public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default) =>
        repo.GetAccountsDueForAnonymizationAsync(now, ct);

    public async Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await repo.SetPreferredLanguageAsync(userId, preferredLanguage, ct);
    }

    public async Task RecordLoginAsync(Guid userId, CancellationToken ct = default)
    {
        await repo.SetLastLoginAsync(userId, clock.GetCurrentInstant(), ct);
    }

    public async Task MarkFakeAsync(Guid userId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        if (await repo.SetMarkedFakeAsync(userId, clock.GetCurrentInstant(), ct))
            await audit.LogAsync(UsersAuditActions.AccountMarkedFake, nameof(User), userId, reason, actorUserId);
    }

    public Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default) =>
        repo.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, eligibleAfter, ct);

    public Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default) =>
        repo.ClearDeletionAsync(userId, ct);

    public async Task<bool> EnsureStubProfileAsync(
        Guid userId,
        string? burnerName = null,
        string? firstName = null,
        string? lastName = null,
        CancellationToken ct = default,
        Guid? actorUserId = null)
    {
        using var _ = await ProfileStubLockFor(userId).AcquireAsync(logger, ct);

        var existing = await repo.GetByUserIdAsync(userId, ct);
        if (existing is not null) return false;

        var info = await GetUserInfoAsync(userId, ct);
        if (info is null)
            return false;

        if (info.IsTombstone)
        {
            logger.LogError(
                "EnsureStubProfileAsync called for tombstone user {UserId} (MergedAt={MergedAt}) - refusing to create Stub Profile",
                userId, info.MergedAt);
            return false;
        }

        // OAuth/import creation seeds User.BurnerName before any Profile exists; inherit it, or
        // the Profile->User mirror below would wipe it. User legal names can't be set yet.
        var user = await repo.GetByIdAsync(userId, ct);
        var resolvedBurnerName = burnerName ?? user?.BurnerName;

        var now = clock.GetCurrentInstant();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            BurnerName = (resolvedBurnerName ?? string.Empty).Trim(),
            FirstName = (firstName ?? string.Empty).Trim(),
            LastName = (lastName ?? string.Empty).Trim(),
        };

        // Seeded names (magic-link signup) promote straight to Active; import/OAuth stubs carry no
        // legal names and stay Bare. The repository derives User.State.
        await repo.AddAsync(profile, ct);
        if (actorUserId.HasValue)
            await audit.LogAsync(UsersAuditActions.StubProfileBackfilled, nameof(User), userId,
                "Created missing profile through admin backfill.", actorUserId.Value);
        return true;
    }

    public async Task<OnboardingResult> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension = false, CancellationToken ct = default)
    {
        if (await repo.GetByUserIdAsync(userId, ct) is null)
            return new OnboardingResult(false, "NotFound");

        // users.State alone: the reason goes to the audit log and the notification, not the row.
        await repo.SetSuspensionAsync(userId, suspended, adminSuspension, ct);
        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> SetRejectedAsync(Guid userId, CancellationToken ct = default)
    {
        if (await repo.GetByUserIdAsync(userId, ct) is null)
            return new OnboardingResult(false, "NotFound");

        // users.State alone: the reason, time and reviewer are Consent's review.
        await repo.SetRejectedAsync(userId, ct);
        return new OnboardingResult(true);
    }

    public async Task<UserProfileSaveResult> SaveProfileAsync(
        Guid userId,
        UserProfileSaveCommand command,
        byte[]? profilePictureData = null,
        CancellationToken ct = default,
        IReadOnlyList<CVEntry>? volunteerHistory = null)
    {
        using var _ = await ProfileStubLockFor(userId).AcquireAsync(logger, ct);

        var now = clock.GetCurrentInstant();
        var profile = await repo.GetByUserIdAsync(userId, ct);

        if (profile is null)
        {
            profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await repo.AddAsync(profile, ct);
        }

        var previousPictureKey = ProfilePictureKeys.Resolve(
            profile.Id, profile.ProfilePictureContentType, profile.ProfilePictureStorageKey);

        profile.BurnerName = command.BurnerName;
        profile.FirstName = command.FirstName;
        profile.LastName = command.LastName;
        profile.City = command.City;
        profile.CountryCode = command.CountryCode;
        profile.Latitude = command.Latitude;
        profile.Longitude = command.Longitude;
        profile.PlaceId = command.PlaceId;
        profile.Bio = command.Bio?.TrimEnd();
        profile.Pronouns = command.Pronouns;
        profile.ContributionInterests = command.ContributionInterests?.TrimEnd();
        profile.BoardNotes = command.BoardNotes?.TrimEnd();
        profile.EmergencyContactName = command.EmergencyContactName;
        profile.EmergencyContactPhone = command.EmergencyContactPhone;
        profile.EmergencyContactRelationship = command.EmergencyContactRelationship;
        profile.NoPriorBurnExperience = command.NoPriorBurnExperience;

        profile.UpdatedAt = now;

        // Year 4 is a leap year, so Feb 29 validates.
        if (command.BirthdayMonth is >= 1 and <= 12 && command.BirthdayDay is >= 1 and <= 31)
        {
            try
            {
                profile.DateOfBirth = new LocalDate(4, command.BirthdayMonth.Value, command.BirthdayDay.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                profile.DateOfBirth = null;
            }
        }
        else
        {
            profile.DateOfBirth = null;
        }

        // Every upload lands on a fresh key, so the previous bytes survive until the editor deletes
        // them after this save commits.
        var pictureMutation = command.PictureMutation;
        string? newPictureKey = null;
        if (pictureMutation == UserProfilePictureMutation.Set
            && profilePictureData is not null && command.ProfilePictureContentType is not null)
        {
            try
            {
                newPictureKey = ProfilePictureKeys.NewVersioned(profile.Id, command.ProfilePictureContentType);
                await fileStorage.SaveAsync(newPictureKey, profilePictureData, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to write profile picture for {ProfileId}; preserving the previous picture metadata",
                    profile.Id);
                newPictureKey = null;
                pictureMutation = UserProfilePictureMutation.None;
            }
        }

        switch (pictureMutation)
        {
            case UserProfilePictureMutation.Remove:
                profile.ProfilePictureContentType = null;
                profile.ProfilePictureStorageKey = null;
                break;
            case UserProfilePictureMutation.Set:
                profile.ProfilePictureContentType = command.ProfilePictureContentType;
                profile.ProfilePictureStorageKey = newPictureKey;
                break;
        }

        // The repository re-derives User.State (Bare->Active) and carries a suspension forward.
        try
        {
            await repo.UpdateAsync(profile, ct, volunteerHistory);
        }
        catch when (newPictureKey is not null)
        {
            await DeleteUnreferencedPictureAsync(profile.Id, newPictureKey);
            throw;
        }

        // Delete the superseded file before the fallible post-commit steps below, so a throw there
        // cannot orphan it (the old key is no longer in the DB).
        var currentPictureKey = ProfilePictureKeys.Resolve(
            profile.Id, profile.ProfilePictureContentType, profile.ProfilePictureStorageKey);
        if (previousPictureKey is not null
            && !string.Equals(previousPictureKey, currentPictureKey, StringComparison.Ordinal))
        {
            await DeleteSupersededPictureAsync(profile.Id, previousPictureKey);
        }

        return new UserProfileSaveResult(profile.Id, previousPictureKey, currentPictureKey);
    }

    private async Task DeleteSupersededPictureAsync(Guid profileId, string key)
    {
        try
        {
            await fileStorage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to delete superseded profile picture for {ProfileId}; current picture metadata is already committed",
                profileId);
        }
    }

    // A failed save may have committed. Verify its persisted reference before deleting new bytes.
    private async Task DeleteUnreferencedPictureAsync(Guid profileId, string key)
    {
        try
        {
            var picture = await repo.GetProfilePictureLocationAsync(profileId, CancellationToken.None);
            if (picture is { } current && string.Equals(
                ProfilePictureKeys.Resolve(profileId, current.ContentType, current.StorageKey), key,
                StringComparison.Ordinal))
                return;

            await fileStorage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to verify or clean up profile picture for {ProfileId} after a failed save", profileId);
        }
    }

    public async Task SaveMedicalConditionsAsync(
        Guid userId,
        string? medicalConditions,
        CancellationToken ct = default)
    {
        using var _ = await ProfileStubLockFor(userId).AcquireAsync(logger, ct);

        var now = clock.GetCurrentInstant();
        var profile = await repo.GetByUserIdAsync(userId, ct);
        if (profile is null)
        {
            profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await repo.AddAsync(profile, ct);
        }

        profile.MedicalConditions = medicalConditions;
        profile.UpdatedAt = now;

        await repo.UpdateAsync(profile, ct);
    }

    internal async Task<UserProfileAnonymizeResult> AnonymizeProfileForDeletionAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        return new UserProfileAnonymizeResult(await repo.AnonymizeForDeletionByUserIdAsync(userId, ct));
    }

    public async Task<UserProfileLanguagesSaveResult> SaveProfileLanguagesAsync(
        Guid profileId,
        IReadOnlyList<ProfileLanguageInfo> languages,
        CancellationToken ct = default)
    {
        var rows = languages
            .Select(l => new ProfileLanguage
            {
                Id = l.Id == Guid.Empty ? Guid.NewGuid() : l.Id,
                ProfileId = profileId,
                LanguageCode = l.LanguageCode,
                Proficiency = l.Proficiency,
            })
            .ToList();

        var ownerUserId = await repo.GetOwnerUserIdAsync(profileId, ct);
        await repo.ReplaceLanguagesAsync(profileId, rows, ct);
        return new UserProfileLanguagesSaveResult(
            ownerUserId is not null,
            ownerUserId);
    }

    public Task<IReadOnlySet<Guid>> SuspendProfilesForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default) =>
        repo.SuspendManyAsync(userIds, ct);

    public Task<UserEmailAddResult> AddUserEmailAsync(
        Guid userId, UserEmailAddCommand command, CancellationToken ct = default) =>
        emailMutations.AddUserEmailAsync(userId, command, ct);

    public Task<UpdateEmailResult> UpdateUserEmailAsync(
        Guid userId, Guid emailId, UserEmailUpdateCommand command, CancellationToken ct = default) =>
        emailMutations.UpdateUserEmailAsync(userId, emailId, command, ct);

    public Task<bool> RemoveUserEmailAsync(
        Guid userId, Guid emailId, UserEmailRemoveCommand command, CancellationToken ct = default) =>
        emailMutations.RemoveUserEmailAsync(userId, emailId, command, ct);

    public async Task<UserEmailReconcilePlanResult> ApplyUserEmailReconcilePlanAsync(
        Guid userId,
        UserEmailReconcilePlanCommand command,
        CancellationToken ct = default)
    {
        var user = await repo.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("User not found.");
        if (UserStateEvaluator.IsTombstone(user))
            throw new InvalidOperationException("Cannot reconcile email addresses for a deleted or merged account.");

        await repo.ApplyUserEmailReconcilePlanAsync(
            displacedRowToDelete: command.DisplacedRowToDelete,
            rowToDelete: command.RowToDelete,
            rowToUpdate: command.RowToUpdate,
            rowToInsert: command.RowToInsert,
            ct);

        var mutatedUserIds = new HashSet<Guid> { userId };
        if (command.DisplacedRowToDelete is not null)
            mutatedUserIds.Add(command.DisplacedRowToDelete.UserId);

        foreach (var mutatedUserId in mutatedUserIds)
        {
            await emailMutations.EnsurePrimaryInvariantAsync(mutatedUserId, ct);
        }

        return new UserEmailReconcilePlanResult(mutatedUserIds);
    }

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        dataExport.ContributeForUserAsync(userId, ct);

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Account] =
                "Partially retained: the User row survives as a tombstone (its id, the display " +
                "name \"Deleted User\" and a deleted-<id>@deleted.local address) so every other " +
                "section's user-id reference stays resolvable and the erasure itself remains " +
                "provable — GDPR Art. 5(2) accountability. Every identity field on it is " +
                "overwritten and the account is locked out permanently.",
            [Profile] = null,
            [MergedProfiles] = null,
            [ContactFields] = null,
            [UserEmails] = null,
            [VolunteerHistory] = null,
            [Languages] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Owns the account record: erased after every other contributor.</summary>
    public bool ErasesLast => true;

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var profile = await repo.GetByUserIdReadOnlyAsync(userId, ct);
        if (profile is not null && ProfilePictureKeys.Resolve(
                profile.Id, profile.ProfilePictureContentType, profile.ProfilePictureStorageKey) is { } pictureKey)
        {
            // Retain the only retryable file reference until deletion succeeds.
            await fileStorage.DeleteAsync(pictureKey, ct);
        }

        await AnonymizeProfileForDeletionAsync(userId, ct);
        await repo.EraseProfileExtrasForUserAsync(userId, ct);
        await ApplyExpiredDeletionAnonymizationAsync(userId, ct);
    }

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        return await repo.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct);
    }

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        _ = actorUserId;
        await repo.ReassignLoginsToUserAsync(mergedFromUserId, mergedToUserId, ct);
        var previousPicture = await repo.ReassignSubAggregatesToUserAsync(mergedFromUserId, mergedToUserId, now, ct);
        if (previousPicture is { } picture &&
            ProfilePictureKeys.Resolve(picture.ProfileId, picture.ContentType, picture.StorageKey) is { } key)
        {
            await DeleteSupersededPictureAsync(picture.ProfileId, key);
        }
    }

    public async Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default)
    {
        var loginUserIds = await repo.GetUserIdsWithExternalLoginsAsync(ct);
        if (loginUserIds.Count == 0)
            return [];

        var withEmail = await repo.GetUserIdsHavingAnyUserEmailAsync(loginUserIds, ct);
        var withEmailSet = withEmail.ToHashSet();
        return loginUserIds.Where(id => !withEmailSet.Contains(id)).ToList();
    }

    public async Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(ct);
        await repo.RemoveAllUserEmailsForUsersAndSaveAsync(userIds, ct);
        return await repo.DeleteUsersAsync(userIds, ct);
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        repo.GetExternalLoginsByUserIdsAsync(userIds, ct);
}
