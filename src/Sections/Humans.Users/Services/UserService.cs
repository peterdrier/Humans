using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using System.ComponentModel.DataAnnotations;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Onboarding.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Domain;
using Humans.Users.Contracts;
using Humans.Base.Threading;
using Humans.Base.Helpers;
using Humans.Gdpr.Contracts;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class UserService(
    IUserRepository repo,
    ICommunicationPreferenceRepository communicationPreferenceRepo,
    IAdminAuthorizationService adminAuthorization,
    IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
    IFileStorage fileStorage,
    IClock clock,
    ILogger<UserService> logger,
    IAuditLogService audit) : IUserServiceInternal, IUserDataContributor
{
    /// <summary>GDPR export JSON keys for this contributor's data.</summary>
    internal const string Account = "Account";
    internal const string Profile = "Profile";
    internal const string MergedProfiles = "MergedProfiles";
    internal const string ContactFields = "ContactFields";
    internal const string UserEmails = "UserEmails";
    internal const string VolunteerHistory = "VolunteerHistory";
    internal const string Languages = "Languages";
    internal const string CommunicationPreferences = "CommunicationPreferences";
    internal const string EventParticipations = "EventParticipations";

    private static readonly TrackedLock EmailAdditionLock = new("UserService.EmailAddition");

    private static readonly TrackedLock[] ProfileStubLocks = Enumerable
        .Range(0, 32)
        .Select(i => new TrackedLock($"UserService.ProfileStub[{i}]"))
        .ToArray();

    private static TrackedLock ProfileStubLockFor(Guid userId) =>
        ProfileStubLocks[(uint)userId.GetHashCode() % (uint)ProfileStubLocks.Length];

    // --- User reads ---

    /// <summary>
    /// The inner service reads rows, it does not resolve merge chains — only
    /// <c>CachingUserService</c> sees the whole graph. So raw and redirecting reads
    /// are the same thing here, and the redirect is tested through the decorator.
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
        var participations = await repo.GetEventParticipationsByUserIdAsync(userId, ct);
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

        var communicationPreferences = await communicationPreferenceRepo
            .GetByUserIdReadOnlyAsync(userId, ct);

        var info = UserInfoFactory.Create(
            user, userEmails, participations, externalLogins,
            profile, contactFields, languages, volunteerHistory,
            communicationPreferences);

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

        var participationsByUser = await repo.GetEventParticipationsByUserIdsAsync(userIds, ct);

        var allPreferences = await communicationPreferenceRepo.GetAllAsync(ct);
        var preferencesByUser = allPreferences
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CommunicationPreference>)g.ToList());

        var result = new List<UserInfo>(users.Count);
        foreach (var user in users)
        {
            var emails = emailsByUser.TryGetValue(user.Id, out var es) ? es : [];
            var logins = loginsByUser.TryGetValue(user.Id, out var ls) ? ls : [];
            var participations = participationsByUser.TryGetValue(user.Id, out var ps)
                ? ps
                : (IReadOnlyList<EventParticipation>)[];

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

            var preferences = preferencesByUser.TryGetValue(user.Id, out var pp) ? pp : [];

            var info = UserInfoFactory.Create(
                user, emails, participations, logins,
                profile, contactFields, languages, volunteerHistory,
                preferences);
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
            "it indicates a DI registration mistake — IUserService should resolve " +
            "to CachingUserService.");

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Own-data only — cross-section cascade lives in IAccountDeletionService.
        if (await repo.GetByIdAsync(userId, ct) is null)
            return null;

        await repo.RemoveAllUserEmailsForUserAndSaveAsync(userId, ct);
        return await repo.ApplyExpiredDeletionAnonymizationAsync(userId, ct);
    }

    /// <summary>
    /// Resolves a user by a verified <c>user_emails</c> address (and its gmail/googlemail
    /// alternate form), returning the matching <see cref="UserInfo"/> or null.
    /// </summary>
    /// <remarks>
    /// Verified-only is deliberate and load-bearing: this method answers "which account owns
    /// this address" for Google sync, and an unverified address must never resolve to an account.
    /// The warmed-snapshot match that logically precedes it is owned by <c>CachingUserService</c>,
    /// which scans its <see cref="UserInfo"/> snapshot and only delegates here on a miss — same
    /// division of labour as <see cref="SearchUsersAsync"/>. Doing the match here too would
    /// re-derive that entire snapshot (<see cref="GetAllUserInfosAsync"/> = six bulk repository
    /// reads) on every miss, once per address, which is what a Google sync run with unknown
    /// recipients actually hits. Callers resolve <c>IUserService</c> to the decorator; reaching
    /// the inner service directly is a DI registration mistake.
    /// See nobodies-collective/Humans#1102.
    /// </remarks>
    public async Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        var normalized = EmailNormalization.NormalizeForComparison(email);
        var alternate = GetAlternateEmail(normalized);

        // The alternate is passed to the query because its ILIKE matches the literal stored
        // string; the in-memory re-check needs only one pass, since EmailsMatch folds
        // gmail/googlemail on both sides (same reasoning as UserEmailService.FindByAddressAsync).
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

    // --- User writes ---

    public async Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default, Guid? googleEmailId = null)
    {
        // Vendor results belong to the row that was attempted, even if the user switched
        // Google address while the request ran. Legacy callers may still select the current
        // verified Google/OAuth address by omitting the row id.
        var emails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        var googleRow = googleEmailId is { } targetId
            ? emails.FirstOrDefault(e => e.Id == targetId && e.IsVerified)
            : emails.FirstOrDefault(e => e.IsVerified && e.IsGoogle)
                ?? emails.Where(e => e.IsVerified && e.Provider != null)
                    .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (googleRow is null)
            return false;

        // Rejected is terminal for a given address: a later successful sync MUST NOT silently flip
        // it back to Valid. The user clears it by selecting a different Google email, which lands on
        // a different row that starts Unknown — so the rejection never strands sync again.
        if (status == GoogleEmailStatus.Valid && googleRow.GoogleEmailStatus == GoogleEmailStatus.Rejected)
            return false;

        if (googleRow.GoogleEmailStatus == status)
            return false;

        googleRow.GoogleEmailStatus = status;
        googleRow.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateUserEmailAsync(googleRow, ct);
        return true;
    }

    public async Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await repo.SetPreferredLanguageAsync(userId, preferredLanguage, ct);
    }

    public async Task RecordLoginAsync(Guid userId, CancellationToken ct = default)
    {
        await repo.SetLastLoginAsync(userId, clock.GetCurrentInstant(), ct);
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        var updated = await repo.SetDeletionPendingAsync(
            userId, requestedAt, scheduledFor, eligibleAfter, ct);
        if (updated)
            InvalidateClaims(userId);
        return updated;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var updated = await repo.ClearDeletionAsync(userId, ct);
        if (updated)
            InvalidateClaims(userId);
        return updated;
    }

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

        // OAuth/import creation seeds User.BurnerName directly (before any Profile exists),
        // so a caller that passes no burnerName still gets the one already on the User row —
        // otherwise this stub write would wipe it via the #1097 Profile->User mirror below.
        // User.FirstName/LastName can't have a value yet at this point (they're only ever
        // set by that same mirror, which requires a Profile to exist), so there's nothing to
        // inherit for those.
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

        // Seeded names (magic-link signup) promote straight to Active, mirroring
        // SaveProfileAsync; import/OAuth paths inherit BurnerName above but still pass no
        // FirstName/LastName, so they stay Bare. The repository derives User.State from the
        // row it persists.
        await repo.AddAsync(profile, ct);
        if (actorUserId.HasValue)
            await audit.LogAsync(AuditAction.StubProfileBackfilled, nameof(User), userId,
                "Created missing profile through admin backfill.", actorUserId.Value);
        InvalidateClaims(userId);
        return true;
    }

    public async Task<bool> SetMembershipTierAsync(
        Guid userId,
        MembershipTier tier,
        CancellationToken ct = default)
    {
        var profile = await repo.GetByUserIdAsync(userId, ct);
        if (profile is null)
        {
            logger.LogWarning(
                "Cannot set membership tier for user {UserId} - no profile exists", userId);
            return false;
        }

        profile.MembershipTier = tier;
        profile.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateAsync(profile, ct);
        return true;
    }

    public async Task<OnboardingResult> ApplyProfileOnboardingMutationAsync(
        Guid userId,
        UserProfileOnboardingCommand command,
        CancellationToken ct = default)
    {
        switch (command.Mutation)
        {
            case UserProfileOnboardingMutation.RecordConsentCheck
                when command.ConsentCheckStatus is not ConsentCheckStatus.Cleared and not ConsentCheckStatus.Flagged:
                throw new ArgumentException(
                    "RecordConsentCheck only accepts Cleared or Flagged; use SetConsentCheckPending for the system-driven Pending transition.",
                    nameof(command));

            case UserProfileOnboardingMutation.SetSuspension when command.Suspended is null:
                throw new ArgumentException("SetSuspension requires Suspended.", nameof(command));
        }

        var profile = await repo.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        var now = clock.GetCurrentInstant();
        switch (command.Mutation)
        {
            case UserProfileOnboardingMutation.RecordConsentCheck:
                if (command.ConsentCheckStatus == ConsentCheckStatus.Cleared && profile.RejectedAt is not null)
                    return new OnboardingResult(false, "AlreadyRejected");
                var cleared = command.ConsentCheckStatus == ConsentCheckStatus.Cleared;
                profile.ConsentCheckStatus = command.ConsentCheckStatus;
                profile.ConsentCheckAt = now;
                profile.ConsentCheckedByUserId = command.ActorUserId;
                profile.ConsentCheckNotes = command.Notes;
                profile.IsApproved = cleared;
                profile.UpdatedAt = now;
                break;

            case UserProfileOnboardingMutation.RejectSignup:
                if (profile.RejectedAt is not null)
                    return new OnboardingResult(false, "AlreadyRejected");
                profile.RejectionReason = command.RejectionReason;
                profile.RejectedAt = now;
                profile.RejectedByUserId = command.ActorUserId;
                profile.IsApproved = false;
                profile.UpdatedAt = now;
                break;

            case UserProfileOnboardingMutation.SetSuspension:
                // Nothing on the profile: suspension is users.State alone. command.Notes goes to
                // the audit log and the notification via HumanLifecycleService, not onto the row.
                break;

            case UserProfileOnboardingMutation.SetConsentCheckPending:
                profile.ConsentCheckStatus = ConsentCheckStatus.Pending;
                profile.UpdatedAt = now;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Mutation, "Unknown profile onboarding mutation.");
        }

        if (command.Mutation == UserProfileOnboardingMutation.SetSuspension)
            await repo.SetSuspensionAsync(
                userId, command.Suspended!.Value, command.AdminSuspension, ct);
        else
            await repo.UpdateAsync(profile, ct);

        InvalidateClaims(userId);
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

        // Edit page owns meal preference + allergies (not intolerances/medical —
        // those are written via SaveDietaryMedicalAsync). Only touch these when
        // the command carries them, so a non-dietary save path can't clobber.
        if (command.DietaryPreference is not null || command.Allergies is not null || command.AllergyOtherText is not null)
        {
            profile.DietaryPreference = string.IsNullOrWhiteSpace(command.DietaryPreference) ? null : command.DietaryPreference;
            profile.Allergies = command.Allergies ?? [];
            profile.AllergyOtherText = command.AllergyOtherText;
        }

        profile.UpdatedAt = now;

        // LocalDate year=4 lets Feb 29 validate.
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

        // Bare->Active promotion happens in the repository, which re-derives User.State from the
        // saved names and carries any existing suspension forward.
        try
        {
            await repo.UpdateAsync(profile, ct, volunteerHistory, command.DisplayName);
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

        InvalidateClaims(userId);

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

    // The metadata save failed, so the freshly written file is unreferenced; the previous picture is untouched.
    private async Task DeleteUnreferencedPictureAsync(Guid profileId, string key)
    {
        try
        {
            await fileStorage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to delete unreferenced profile picture for {ProfileId} after a failed save", profileId);
        }
    }

    public async Task SaveDietaryMedicalAsync(
        Guid userId,
        UserProfileDietaryMedicalCommand command,
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

        profile.DietaryPreference = command.DietaryPreference;
        profile.Allergies = command.Allergies;
        profile.AllergyOtherText = command.AllergyOtherText;
        profile.Intolerances = command.Intolerances;
        profile.IntoleranceOtherText = command.IntoleranceOtherText;
        profile.MedicalConditions = command.MedicalConditions;
        profile.UpdatedAt = now;

        await repo.UpdateAsync(profile, ct);
    }

    public async Task<UserProfileAnonymizeResult> AnonymizeProfileForDeletionAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var profile = await repo.GetByUserIdReadOnlyAsync(userId, ct);
        var anonymized = await repo.AnonymizeForDeletionByUserIdAsync(userId, ct);
        return new UserProfileAnonymizeResult(
            anonymized,
            profile?.Id,
            profile is null
                ? null
                : ProfilePictureKeys.Resolve(
                    profile.Id, profile.ProfilePictureContentType, profile.ProfilePictureStorageKey));
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

    public async Task<bool> SetProfileIbanAsync(Guid userId, string? iban, CancellationToken ct = default)
    {
        var profile = await repo.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return false;

        profile.Iban = string.IsNullOrWhiteSpace(iban) ? null : IbanValidator.Normalize(iban);
        profile.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateAsync(profile, ct);
        return true;
    }

    public Task<IReadOnlySet<Guid>> SuspendProfilesForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default) =>
        repo.SuspendManyAsync(userIds, ct);

    public Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeMembershipTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default) =>
        repo.DowngradeTierForExpiredAsync(
            currentTier, userIdsToKeep, fallbackTierByUser, now, ct);

    public async Task<UserEmailAddResult> AddUserEmailAsync(
        Guid userId,
        UserEmailAddCommand command,
        CancellationToken ct = default)
    {
        var email = command.Email.Trim();
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateEmail(normalizedEmail);

        if (!new EmailAddressAttribute().IsValid(email))
            throw new ValidationException("Please enter a valid email address.");

        using var addition = await EmailAdditionLock.AcquireAsync(logger, ct);
        var rows = await repo.GetUserEmailsByAddressAsync(normalizedEmail, alternateEmail, ct);
        var existing = rows.FirstOrDefault(e => e.UserId == userId);
        if (existing is not null)
        {
            if (command.IgnoreExisting)
                return new UserEmailAddResult(existing.Id, Added: false, IsConflict: false);

            throw new ValidationException("This email address is already in your account.");
        }

        var isConflict = rows.Any(e => e.UserId != userId && e.IsVerified);

        var user = await repo.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("User not found.");
        if (UserStateEvaluator.IsTombstone(user))
            throw new InvalidOperationException("Cannot add email addresses to a deleted or merged account.");

        var now = clock.GetCurrentInstant();
        var row = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = command.IsVerified,
            IsPrimary = false,
            IsGoogle = false,
            Visibility = command.Visibility,
            Provider = command.Provider,
            ProviderKey = command.ProviderKey,
            VerificationSentAt = command.VerificationSentAt,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repo.AddUserEmailAsync(row, ct);
        // The row has committed; finish its required invariant repairs after a browser disconnect.
        await EnsurePrimaryInvariantAsync(row.UserId, CancellationToken.None);
        await EnsureGoogleInvariantAsync(row.UserId, CancellationToken.None);
        return new UserEmailAddResult(row.Id, Added: true, isConflict);
    }

    public async Task<UpdateEmailResult> UpdateUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailUpdateCommand command,
        CancellationToken ct = default)
    {
        var changed = false;

        if (command.MarkVerified)
        {
            var email = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
            if (email is null) return new UpdateEmailResult(false, "Users_EmailUpdate_NotFound");

            if (email.IsVerified || email.Provider is not null)
                return new UpdateEmailResult(false, "Profile_InvalidVerificationLink");

            email.IsVerified = true;
            email.UpdatedAt = clock.GetCurrentInstant();
            await repo.UpdateUserEmailAsync(email, ct);
            // Verification has committed; primary / Google repair is part of completing it.
            await EnsurePrimaryInvariantAsync(userId, CancellationToken.None);
            await EnsureGoogleInvariantAsync(userId, CancellationToken.None);
            changed = true;
        }

        if (command.Primary == UserEmailPrimaryChange.MakePrimary)
        {
            var primary = await SetPrimaryEmailAsync(userId, emailId, ct);
            if (!primary.Succeeded) return primary;
            changed = true;
        }
        else if (command.Primary == UserEmailPrimaryChange.ClearDuplicatePrimary)
        {
            changed |= await ClearDuplicatePrimaryAsync(userId, emailId, ct);
        }

        if (command.Google == UserEmailGoogleChange.MakeGoogle)
        {
            var row = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
            if (row is null || !row.IsVerified) return new UpdateEmailResult(false);
            await repo.SetUserEmailGoogleExclusiveAsync(userId, emailId, clock.GetCurrentInstant(), ct);
            changed = true;
        }
        else if (command.Google == UserEmailGoogleChange.ClearDuplicateGoogle)
        {
            changed |= await ClearDuplicateGoogleAsync(userId, emailId, ct);
        }

        if (command.ChangeVisibility)
        {
            var row = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
            if (row is null) return new UpdateEmailResult(false, "Users_EmailUpdate_NotFound");
            row.Visibility = command.Visibility;
            row.UpdatedAt = clock.GetCurrentInstant();
            await repo.UpdateUserEmailAsync(row, ct);
            changed = true;
        }

        return new UpdateEmailResult(changed);
    }

    public async Task<bool> RemoveUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailRemoveCommand command,
        CancellationToken ct = default)
    {
        var email = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct)
            ?? throw new InvalidOperationException("Email not found.");

        var hasProvider = !string.IsNullOrEmpty(email.Provider);
        if (command.Mode == UserEmailRemovalMode.PlainEmail && hasProvider)
            return false;
        if (command.Mode == UserEmailRemovalMode.ProviderLinkedEmail && !hasProvider)
            return false;

        if (command.PreserveLastVerifiedEmail && email.IsVerified)
        {
            var allEmails = await repo.GetUserEmailsByUserIdForMutationAsync(userId, ct);
            var verifiedRemaining = allEmails.Count(e => e.IsVerified && e.Id != emailId);

            if (verifiedRemaining == 0)
            {
                throw new ValidationException(
                    "Cannot remove your last verified email. Add another verified email first " +
                    "so you can still receive system notifications.");
            }
        }

        await repo.RemoveUserEmailAsync(email, ct);

        if (command.RepairInvariants)
        {
            // Removal has committed; leave the remaining verified addresses usable.
            await EnsurePrimaryInvariantAsync(userId, CancellationToken.None);
            await EnsureGoogleInvariantAsync(userId, CancellationToken.None);
        }

        return true;
    }

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
            await EnsurePrimaryInvariantAsync(mutatedUserId, ct);
            await EnsureGoogleInvariantAsync(mutatedUserId, ct);
        }

        return new UserEmailReconcilePlanResult(mutatedUserIds);
    }

    public Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default) =>
        repo.SetLastConsentReminderSentAsync(userId, sentAt, ct);

    // --- EventParticipation reads ---

    public Task<IReadOnlyList<UserParticipationRow>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "GetAllParticipationsForYearAsync is only meaningful through CachingUserService — " +
            "projects the year's participations from the cached UserInfo snapshot. If this is " +
            "being called on the inner UserService it indicates a DI registration mistake.");

    // --- EventParticipation writes ---

    public async Task DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var persisted = await repo.UpsertParticipationAsync(
            userId, year, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared,
            declaredAt: now, checkedInAt: null, ct);

        if (persisted is null)
        {
            // Blocked by Attended.
            logger.LogWarning(
                "Cannot declare NotAttending for user {UserId} year {Year} — already Attended",
                userId, year);
            return;
        }

        logger.LogInformation(
            "User {UserId} declared NotAttending for year {Year}",
            userId, year);
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await repo.GetParticipationAsync(userId, year, ct);
        if (existing is null)
            return false;

        if (existing.Status != ParticipationStatus.NotAttending ||
            existing.Source != ParticipationSource.UserDeclared)
        {
            logger.LogWarning(
                "Cannot undo NotAttending for user {UserId} year {Year} — status is {Status} from {Source}",
                userId, year, existing.Status, existing.Source);
            return false;
        }

        var removed = await repo.RemoveParticipationAsync(
            userId, year, ParticipationSource.UserDeclared, ct);

        if (removed)
        {
            logger.LogInformation(
                "User {UserId} undid NotAttending declaration for year {Year}",
                userId, year);
        }

        return removed;
    }

    public Task SetParticipationFromTicketSyncAsync(
        Guid userId, int year, ParticipationStatus status, Instant? checkedInAt, CancellationToken ct = default) =>
        // Attended-is-permanent, source-override and CheckedInAt-never-overwrite
        // semantics live in repo upsert.
        repo.UpsertParticipationAsync(
            userId, year, status, ParticipationSource.TicketSync,
            declaredAt: null, checkedInAt: checkedInAt, ct);

    public Task<IReadOnlyList<OnsiteUserRow>> GetOnsiteUsersAsync(int year, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "GetOnsiteUsersAsync is only meaningful through CachingUserService — it projects " +
            "Attended+CheckedInAt rows from the cached UserInfo snapshot. Hitting the inner " +
            "UserService indicates a DI registration mistake.");

    public Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        repo.RemoveParticipationAsync(userId, year, ParticipationSource.TicketSync, ct);

    public async Task<int> BackfillParticipationsAsync(
        int year,
        List<(Guid UserId, ParticipationStatus Status)> entries,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var changed = await repo.BackfillParticipationsAsync(year, entries, ct);
        foreach (var (userId, status) in changed)
            await audit.LogAsync(AuditAction.ParticipationBackfilled, nameof(User), userId,
                $"Recorded {status} participation for {year} from admin backfill.", actorUserId);
        var count = changed.Count;
        logger.LogInformation(
            "Backfilled {Count} participation records for year {Year}",
            count, year);
        return count;
    }

    // --- IUserDataContributor — GDPR export ---

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var allUsers = (await repo.GetAllAsync(ct)).ToDictionary(u => u.Id);
        if (!allUsers.TryGetValue(userId, out var user))
        {
            return [
                new UserDataSlice(Account, null),
                new UserDataSlice(Profile, null)
            ];
        }

        var visited = new HashSet<Guid> { userId };
        while (user.MergedToUserId is { } next && visited.Add(next) &&
               allUsers.TryGetValue(next, out var target))
        {
            user = target;
        }
        userId = user.Id;

        // see nobodies-collective/Humans#687 — User.GoogleEmail deprecated, not exported.
        var userEmails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        var googleEmail = userEmails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault();
        var effectiveEmail = userEmails
            .Where(e => e.IsVerified)
            .OrderByDescending(e => e.IsPrimary)
            .Select(e => e.Email)
            .FirstOrDefault() ?? user.IdentityEmailColumn;

        var shaped = new
        {
            user.Id,
            Email = effectiveEmail,
            user.DisplayName,
            user.PreferredLanguage,
            GoogleEmail = googleEmail,
            user.UnsubscribedFromCampaigns,
            user.SuppressScheduleChangeEmails,
            ContactSource = user.ContactSource?.ToString(),
            State = user.State.ToString(),
            DeletionRequestedAt = user.DeletionRequestedAt.ToIso8601(),
            DeletionScheduledFor = user.DeletionScheduledFor.ToIso8601(),
            DeletionEligibleAfter = user.DeletionEligibleAfter.ToIso8601(),
            CreatedAt = user.CreatedAt.ToIso8601(),
            LastLoginAt = user.LastLoginAt.ToIso8601()
        };

        var participations = await repo.GetEventParticipationsByUserIdAsync(userId, ct);
        var participationsShaped = participations
            .Select(ep => new
            {
                ep.Year,
                Status = ep.Status.ToString(),
                Source = ep.Source.ToString(),
                DeclaredAt = ep.DeclaredAt.ToIso8601(),
                CheckedInAt = ep.CheckedInAt.ToIso8601()
            })
            .ToList();

        var profile = await repo.GetByUserIdReadOnlyAsync(userId, ct);

        var contactFields = profile is not null
            ? await repo.GetByProfileIdReadOnlyAsync(profile.Id, ct)
            : [];

        var volunteerHistory = profile?.VolunteerHistory
            .OrderByDescending(v => v.Date)
            .ThenByDescending(v => v.CreatedAt)
            .ToList() ?? (IReadOnlyList<VolunteerHistoryEntry>)[];

        var profileLanguages = profile is not null
            ? await repo.GetLanguagesAsync(profile.Id, ct)
            : [];

        var communicationPreferences = await communicationPreferenceRepo
            .GetByUserIdReadOnlyAsync(userId, ct);

        var profileSlice = profile is null
            ? new UserDataSlice(Profile, null)
            : new UserDataSlice(Profile, ShapeProfileForExport(profile,
                user.State is UserState.Suspended or UserState.AdminSuspended));

        // Profile folds deliberately retain health data on archived rows.
        // Walk backwards from the survivor so every retained profile follows it into the export.
        var sourcesByTarget = allUsers.Values.Where(u => u.MergedToUserId is not null)
            .ToLookup(u => u.MergedToUserId!.Value);
        var mergedIds = new HashSet<Guid> { userId };
        var pending = new Queue<Guid>([userId]);
        while (pending.TryDequeue(out var targetId))
        {
            foreach (var source in sourcesByTarget[targetId])
            {
                if (mergedIds.Add(source.Id))
                    pending.Enqueue(source.Id);
            }
        }
        mergedIds.Remove(userId);
        var mergedProfiles = mergedIds.Count == 0 ? [] : await repo.GetAllProfilesAsync(ct);
        var mergedProfileSlice = new UserDataSlice(MergedProfiles, mergedProfiles
            .Where(p => mergedIds.Contains(p.UserId))
            .Select(p => new
            {
                p.UserId,
                Profile = ShapeProfileForExport(p,
                    allUsers[p.UserId].State is UserState.Suspended or UserState.AdminSuspended)
            }).ToList());

        var contactFieldSlice = new UserDataSlice(ContactFields, contactFields.Select(cf => new
        {
            cf.FieldType,
            Label = cf.DisplayLabel,
            cf.Value,
            cf.Visibility
        }).ToList());

        var userEmailsSlice = new UserDataSlice(UserEmails, userEmails.Select(e => new
        {
            e.Email,
            e.IsVerified,
            // JSON keys pinned per memory/code/no-rename-serialized-fields.md (GDPR export stability).
            IsOAuth = e.Provider != null,
            IsNotificationTarget = e.IsPrimary,
            e.IsGoogle,
            GoogleEmailStatus = e.GoogleEmailStatus.ToString(),
            VerificationSentAt = e.VerificationSentAt.ToIso8601(),
            CreatedAt = e.CreatedAt.ToIso8601(),
            UpdatedAt = e.UpdatedAt.ToIso8601(),
            e.Visibility
        }).ToList());

        var volunteerHistorySlice = new UserDataSlice(VolunteerHistory, volunteerHistory.Select(vh => new
        {
            Date = vh.Date.ToInvariantDate(),
            vh.EventName,
            vh.Description,
            CreatedAt = vh.CreatedAt.ToIso8601()
        }).ToList());

        var languagesSlice = new UserDataSlice(Languages, profileLanguages.Select(pl => new
        {
            pl.LanguageCode,
            pl.Proficiency
        }).ToList());

        var commPrefsSlice = new UserDataSlice(CommunicationPreferences, communicationPreferences.Select(cp => new
        {
            cp.Category,
            cp.OptedOut,
            cp.InboxEnabled,
            SubscribedAt = cp.SubscribedAt.ToIso8601(),
            UpdatedAt = cp.UpdatedAt.ToIso8601(),
            cp.UpdateSource
        }).ToList());

        return
        [
            new UserDataSlice(Account, shaped),
            new UserDataSlice(EventParticipations, participationsShaped),
            profileSlice,
            mergedProfileSlice,
            contactFieldSlice,
            userEmailsSlice,
            volunteerHistorySlice,
            languagesSlice,
            commPrefsSlice
        ];
    }

    private static object ShapeProfileForExport(Humans.Users.Domain.Profile profile, bool isSuspended) =>
        new
        {
            profile.BurnerName,
            profile.FirstName,
            profile.LastName,
            Birthday = profile.DateOfBirth is not null
                ? $"{profile.DateOfBirth.Value.Month:D2}-{profile.DateOfBirth.Value.Day:D2}"
                : null,
            profile.City,
            profile.CountryCode,
            profile.Latitude,
            profile.Longitude,
            profile.Bio,
            profile.Pronouns,
            profile.ContributionInterests,
            profile.BoardNotes,
            profile.MembershipTier,
            profile.IsApproved,
            // JSON key pinned per memory/code/no-rename-serialized-fields.md (GDPR export stability).
            IsSuspended = isSuspended,
            profile.NoPriorBurnExperience,
            ConsentCheckStatus = profile.ConsentCheckStatus?.ToString(),
            ConsentCheckAt = profile.ConsentCheckAt.ToIso8601(),
            profile.ConsentCheckNotes,
            profile.RejectionReason,
            RejectedAt = profile.RejectedAt.ToIso8601(),
            profile.EmergencyContactName,
            profile.EmergencyContactPhone,
            profile.EmergencyContactRelationship,
            // Dietary + medical (moved here from VolunteerEventProfile). Included in
            // the data-subject export — this is the owner's own export of their data.
            profile.DietaryPreference,
            profile.Allergies,
            profile.AllergyOtherText,
            profile.Intolerances,
            profile.IntoleranceOtherText,
            profile.MedicalConditions,
            profile.HasCustomProfilePicture,
            CreatedAt = profile.CreatedAt.ToIso8601(),
            UpdatedAt = profile.UpdatedAt.ToIso8601()
        };

    // --- IUserDataContributor — GDPR erasure ---

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Account] =
                "Partially retained: the User row survives as a tombstone (its id, the display " +
                "name \"Deleted User\" and a deleted-<id>@deleted.local address) so every other " +
                "section's user-id reference stays resolvable and the erasure itself remains " +
                "provable — GDPR Art. 5(2) accountability. Every identity field on it is " +
                "overwritten and the account is locked out permanently.",
            [EventParticipations] =
                "Retained: the year and status of each participation, keyed to the tombstoned " +
                "user id, is the association's register of who was a member in which year — " +
                "Ley Orgánica 1/2002 Art. 14, GDPR Art. 17(3)(b). It carries no identity fields.",
            [Profile] = null,
            [MergedProfiles] = null,
            [ContactFields] = null,
            [UserEmails] = null,
            [VolunteerHistory] = null,
            [Languages] = null,
            [CommunicationPreferences] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Owns the account record: erased after every other contributor.</summary>
    public bool ErasesLast => true;

    /// <summary>
    /// Anonymizes the profile (contact fields and volunteer history go with it),
    /// removes the profile-picture bytes, clears the Article 9 health/diet fields
    /// and the language + communication-preference rows, then collapses the User
    /// aggregate's identity and drops every UserEmail and external login.
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var profileAnonymization = await AnonymizeProfileForDeletionAsync(userId, ct);
        if (profileAnonymization.Anonymized &&
            profileAnonymization.ProfileId is { } profileId &&
            profileAnonymization.PreviousProfilePictureKey is { } pictureKey)
        {
            try
            {
                await fileStorage.DeleteAsync(pictureKey, ct);
            }
            catch (Exception ex)
            {
                // A stranded blob must not abort the cascade — the row it hangs off is already gone.
                logger.LogError(ex,
                    "Failed to delete profile picture during GDPR erasure for user {UserId}", userId);
            }
        }

        await repo.EraseProfileExtrasForUserAsync(userId, ct);
        await communicationPreferenceRepo.DeleteAllForUserAsync(userId, ct);
        await ApplyExpiredDeletionAnonymizationAsync(userId, ct);
    }

    private async Task<UpdateEmailResult> SetPrimaryEmailAsync(Guid userId, Guid emailId, CancellationToken ct)
    {
        var emails = (await repo.GetUserEmailsByUserIdForMutationAsync(userId, ct)).ToList();

        var target = emails.FirstOrDefault(e => e.Id == emailId);
        if (target is null) return new UpdateEmailResult(false, "Users_EmailUpdate_NotFound");

        if (!target.IsVerified)
            return new UpdateEmailResult(false, "Users_EmailUpdate_UnverifiedTarget");

        var now = clock.GetCurrentInstant();
        var changed = new List<UserEmail>();
        foreach (var email in emails)
        {
            var shouldBePrimary = email.Id == emailId;
            if (email.IsPrimary != shouldBePrimary)
            {
                email.IsPrimary = shouldBePrimary;
                email.UpdatedAt = now;
                changed.Add(email);
            }
        }

        if (changed.Count > 0)
            await repo.UpdateUserEmailsAsync(changed, ct);
        return new UpdateEmailResult(true);
    }

    private async Task<bool> ClearDuplicatePrimaryAsync(Guid userId, Guid emailId, CancellationToken ct)
    {
        var row = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
        if (row is null || !row.IsPrimary) return false;

        var allEmails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        if (allEmails.Count(e => e.IsPrimary && e.IsVerified) <= 1) return false;

        row.IsPrimary = false;
        row.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateUserEmailAsync(row, ct);
        return true;
    }

    private async Task<bool> ClearDuplicateGoogleAsync(Guid userId, Guid emailId, CancellationToken ct)
    {
        var row = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
        if (row is null || !row.IsGoogle) return false;

        var allEmails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        if (allEmails.Count(e => e.IsGoogle) <= 1) return false;

        row.IsGoogle = false;
        row.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateUserEmailAsync(row, ct);
        return true;
    }

    private async Task EnsurePrimaryInvariantAsync(Guid userId, CancellationToken ct)
    {
        var emails = (await repo.GetUserEmailsByUserIdForMutationAsync(userId, ct)).ToList();
        var verified = emails.Where(e => e.IsVerified).ToList();
        if (verified.Count == 0)
            return;

        var currentPrimaries = verified.Where(e => e.IsPrimary).ToList();

        var winner =
            verified.FirstOrDefault(e => e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            ?? currentPrimaries.OrderBy(e => e.Id).FirstOrDefault()
            ?? verified.OrderByDescending(e => e.UpdatedAt).ThenBy(e => e.Id).First();

        if (currentPrimaries.Count == 1 && currentPrimaries[0].Id == winner.Id)
            return;

        var now = clock.GetCurrentInstant();
        var changed = new List<UserEmail>();
        foreach (var row in verified)
        {
            var shouldBePrimary = row.Id == winner.Id;
            if (row.IsPrimary != shouldBePrimary)
            {
                row.IsPrimary = shouldBePrimary;
                row.UpdatedAt = now;
                changed.Add(row);
            }
        }

        if (changed.Count > 0)
            await repo.UpdateUserEmailsAsync(changed, ct);
    }

    private async Task EnsureGoogleInvariantAsync(Guid userId, CancellationToken ct)
    {
        var emails = (await repo.GetUserEmailsByUserIdForMutationAsync(userId, ct)).ToList();
        var verified = emails.Where(e => e.IsVerified).ToList();
        if (verified.Count == 0)
            return;

        var currentGoogles = verified.Where(e => e.IsGoogle).ToList();

        var winner =
            verified.FirstOrDefault(e => e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            ?? currentGoogles.OrderBy(e => e.Id).FirstOrDefault()
            ?? verified.OrderByDescending(e => e.UpdatedAt).ThenBy(e => e.Id).First();

        if (currentGoogles.Count == 1 && currentGoogles[0].Id == winner.Id)
            return;

        var now = clock.GetCurrentInstant();
        var changed = new List<UserEmail>();
        foreach (var row in verified)
        {
            var shouldBeGoogle = row.Id == winner.Id;
            if (row.IsGoogle != shouldBeGoogle)
            {
                row.IsGoogle = shouldBeGoogle;
                row.UpdatedAt = now;
                changed.Add(row);
            }
        }

        if (changed.Count > 0)
            await repo.UpdateUserEmailsAsync(changed, ct);
    }

    // RoleAssignmentClaimsTransformation caches the UserState claim for 60s, so evict after
    // profile transitions that can change the stored state (e.g. Bare after a name submit).
    private void InvalidateClaims(Guid userId) => roleAssignmentClaimsInvalidator.Invalidate(userId);

    private static string? GetAlternateEmail(string normalizedEmail)
    {
        if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
            return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";

        if (normalizedEmail.EndsWith("@googlemail.com", StringComparison.Ordinal))
            return $"{normalizedEmail[..^"@googlemail.com".Length]}@gmail.com";

        return null;
    }

    // --- Account merge: fold-into-target primitives ---

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        return await repo.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct);
    }

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        // `actorUserId` unused — kept for IUserMerge contract.
        _ = actorUserId;
        await repo.ReassignLoginsToUserAsync(mergedFromUserId, mergedToUserId, ct);
        await repo.ReassignEventParticipationToUserAsync(mergedFromUserId, mergedToUserId, ct);
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
