using System.ComponentModel.DataAnnotations;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users;

public sealed class UserService(
    IUserRepository repo,
    ICommunicationPreferenceRepository communicationPreferenceRepo,
    IAdminAuthorizationService adminAuthorization,
    IClock clock,
    ILogger<UserService> logger) : IUserService, IUserDataContributor, IUserMerge
{
    private static readonly SemaphoreSlim[] ProfileStubLocks = CreateProfileStubLocks(32);
    private static SemaphoreSlim[] CreateProfileStubLocks(int count)
    {
        var locks = new SemaphoreSlim[count];
        for (var i = 0; i < count; i++) locks[i] = new SemaphoreSlim(1, 1);
        return locks;
    }

    private static SemaphoreSlim ProfileStubLockFor(Guid userId) =>
        ProfileStubLocks[(uint)userId.GetHashCode() % (uint)ProfileStubLocks.Length];

    // --- User reads ---

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

        return UserInfo.Create(
            user, userEmails, participations, externalLogins,
            profile, contactFields, languages, volunteerHistory,
            communicationPreferences);
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

            result.Add(UserInfo.Create(
                user, emails, participations, logins,
                profile, contactFields, languages, volunteerHistory,
                preferences));
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

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var users = await repo.GetByIdsAsync(userIds, ct);
        if (users.Count == 0)
            return users;

        await HydrateUserEmailsAsync(users.Values, ct);
        return users;
    }

    public async Task<string?> PurgeOwnDataAsync(Guid userId, CancellationToken ct = default)
    {
        if (await repo.GetByIdAsync(userId, ct) is null)
            return null;

        await repo.RemoveAllUserEmailsForUserAndSaveAsync(userId, ct);
        var displayName = await repo.PurgeAsync(userId, ct);
        if (displayName is null)
            return null;

        logger.LogWarning("Purged human {DisplayName} ({HumanId})", displayName, userId);

        return displayName;
    }

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Own-data only — cross-section cascade lives in IAccountDeletionService.
        if (await repo.GetByIdAsync(userId, ct) is null)
            return null;

        await repo.RemoveAllUserEmailsForUserAndSaveAsync(userId, ct);
        return await repo.ApplyExpiredDeletionAnonymizationAsync(userId, ct);
    }

    public async Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        var normalized = EmailNormalization.NormalizeForComparison(email);
        var alternate = GetAlternateEmail(normalized);

        var matchingUserIds = await repo.GetDistinctVerifiedUserEmailUserIdsAsync(normalized, alternate, ct);
        if (matchingUserIds.Count > 0)
            return await GetUserInfoAsync(matchingUserIds[0], ct);

        var legacyUser = await repo.GetByEmailOrAlternateAsync(normalized, alternate, ct);
        if (legacyUser is null)
            return null;

        return await GetUserInfoAsync(legacyUser.Id, ct);
    }

    private async Task HydrateUserEmailsAsync(IEnumerable<User> users, CancellationToken ct)
    {
        var userList = users.ToList();
        if (userList.Count == 0)
            return;

        var userIds = userList.Select(u => u.Id).ToHashSet();
        var emailsByUser = (await repo.GetAllUserEmailsAsync(ct))
            .Where(e => userIds.Contains(e.UserId))
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UserEmail>)g.ToList());

        foreach (var user in userList)
        {
            var emails = emailsByUser.TryGetValue(user.Id, out var found)
                ? found
                : [];
            AttachUserEmails(user, emails);
        }
    }

    private static void AttachUserEmails(User user, IEnumerable<UserEmail> emails)
    {
        if (user.UserEmails.Count > 0)
            return;

        foreach (var email in emails)
            user.UserEmails.Add(email);
    }

    public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default) =>
        repo.GetAccountsDueForAnonymizationAsync(now, ct);

    // --- User writes ---

    public async Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        if (status == GoogleEmailStatus.Valid)
        {
            var user = await repo.GetByIdAsync(userId, ct);
            if (user is null || user.GoogleEmailStatus == GoogleEmailStatus.Rejected)
                return false;
        }

        return await SetGoogleEmailStatusInternalAsync(userId, status, ct);
    }

    // Private write — Try variant is the only external entry point (sync-driven; Rejected is terminal).
    private async Task<bool> SetGoogleEmailStatusInternalAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        return await repo.SetGoogleEmailStatusAsync(userId, status, ct);
    }

    public async Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await repo.SetPreferredLanguageAsync(userId, preferredLanguage, ct);
    }

    public async Task SetICalTokenAsync(Guid userId, Guid token, CancellationToken ct = default)
    {
        await repo.SetICalTokenAsync(userId, token, ct);
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        var updated = await repo.SetDeletionPendingAsync(
            userId, requestedAt, scheduledFor, eligibleAfter, ct);
        return updated;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        return await repo.ClearDeletionAsync(userId, ct);
    }

    public async Task<bool> EnsureStubProfileAsync(
        Guid userId,
        string? burnerName = null,
        string? firstName = null,
        string? lastName = null,
        CancellationToken ct = default)
    {
        var gate = ProfileStubLockFor(userId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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

            var now = clock.GetCurrentInstant();
            var profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
                BurnerName = (burnerName ?? string.Empty).Trim(),
                FirstName = (firstName ?? string.Empty).Trim(),
                LastName = (lastName ?? string.Empty).Trim(),
            };

            // Seeded names (magic-link signup) promote straight to Active, mirroring
            // SaveProfileAsync; import/OAuth paths pass no names and stay Stub. see #635 / #812.
            profile.State = HasRequiredNameFields(profile) ? ProfileState.Active : ProfileState.Stub;

            await repo.AddAsync(profile, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
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
        ValidateProfileOnboardingCommand(command);

        var profile = await repo.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        var now = clock.GetCurrentInstant();
        switch (command.Mutation)
        {
            case UserProfileOnboardingMutation.RecordConsentCheck:
                if (command.ConsentCheckStatus == ConsentCheckStatus.Cleared && profile.RejectedAt is not null)
                    return new OnboardingResult(false, "AlreadyRejected");
                ApplyConsentCheck(profile, command, now);
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

            case UserProfileOnboardingMutation.ApproveVolunteer:
                profile.IsApproved = true;
                profile.UpdatedAt = now;
                break;

            case UserProfileOnboardingMutation.SetSuspension:
                ApplySuspension(profile, command, now);
                break;

            case UserProfileOnboardingMutation.SetConsentCheckPending:
                profile.ConsentCheckStatus = ConsentCheckStatus.Pending;
                profile.UpdatedAt = now;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Mutation, "Unknown profile onboarding mutation.");
        }

        await repo.UpdateAsync(profile, ct);
        return new OnboardingResult(true);
    }

    public async Task<UserProfileSaveResult> SaveProfileAsync(
        Guid userId,
        UserProfileSaveCommand command,
        CancellationToken ct = default)
    {
        var gate = ProfileStubLockFor(userId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
                    State = ProfileState.Stub,
                };
                await repo.AddAsync(profile, ct);
            }

            var previousPictureContentType = profile.ProfilePictureContentType;

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

            profile.ProfilePictureContentType = command.PictureMutation switch
            {
                UserProfilePictureMutation.Remove => null,
                UserProfilePictureMutation.Set => command.ProfilePictureContentType,
                _ => profile.ProfilePictureContentType,
            };

            // see #635 (section 15i) - Stub->Active promotion (mirrors UserInfo.HasRequiredNameFields).
            if (profile.State != ProfileState.Suspended)
            {
                profile.State = HasRequiredNameFields(profile) ? ProfileState.Active : ProfileState.Stub;
            }

            await repo.UpdateAsync(profile, ct);
            await repo.UpdateDisplayNameAsync(userId, command.DisplayName, ct);

            return new UserProfileSaveResult(
                profile.Id,
                previousPictureContentType,
                profile.ProfilePictureContentType);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveDietaryMedicalAsync(
        Guid userId,
        UserProfileDietaryMedicalCommand command,
        CancellationToken ct = default)
    {
        var gate = ProfileStubLockFor(userId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
                    State = ProfileState.Stub,
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
        finally
        {
            gate.Release();
        }
    }

    public async Task<UserProfilePictureContentTypeResult> SetProfilePictureContentTypeAsync(
        Guid userId,
        string contentType,
        CancellationToken ct = default)
    {
        var profile = await repo.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new UserProfilePictureContentTypeResult(false, null, null, null);

        var previousContentType = profile.ProfilePictureContentType;
        profile.ProfilePictureContentType = contentType;
        profile.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateAsync(profile, ct);

        return new UserProfilePictureContentTypeResult(
            true,
            profile.Id,
            previousContentType,
            contentType);
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
            profile?.ProfilePictureContentType);
    }

    public async Task<bool> SaveProfileVolunteerHistoryAsync(
        Guid userId,
        IReadOnlyList<CVEntry> entries,
        CancellationToken ct = default)
    {
        var profile = await repo.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return false;

        await repo.ReconcileCVEntriesAsync(profile.Id, entries, ct);
        return true;
    }

    public async Task<UserProfileLanguagesSaveResult> SaveProfileLanguagesAsync(
        Guid profileId,
        IReadOnlyList<ProfileLanguage> languages,
        CancellationToken ct = default)
    {
        await repo.ReplaceLanguagesAsync(profileId, languages, ct);
        var ownerUserId = await repo.GetOwnerUserIdAsync(profileId, ct);
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
        Instant now,
        CancellationToken ct = default) =>
        repo.SuspendManyAsync(userIds, now, ct);

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

        var existing = await repo.FindUserEmailByNormalizedEmailAsync(normalizedEmail, alternateEmail, ct);
        if (existing is not null && existing.UserId == userId)
        {
            if (command.IgnoreExisting)
                return new UserEmailAddResult(existing.Id, Added: false, IsConflict: false);

            throw new ValidationException("This email address is already in your account.");
        }

        var isConflict = existing is not null && existing.UserId != userId && existing.IsVerified;

        _ = await repo.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException("User not found.");

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

        await AddRowWithInvariantsAsync(row, ct);
        return new UserEmailAddResult(row.Id, Added: true, isConflict);
    }

    public async Task<bool> UpdateUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailUpdateCommand command,
        CancellationToken ct = default)
    {
        var changed = false;

        if (command.MarkVerified)
        {
            var email = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct)
                ?? throw new InvalidOperationException("Email not found.");

            if (email.IsVerified || email.Provider is not null)
                throw new ValidationException("No email pending verification.");

            email.IsVerified = true;
            email.UpdatedAt = clock.GetCurrentInstant();
            await repo.UpdateUserEmailAsync(email, ct);
            await EnsurePrimaryInvariantAsync(userId, ct);
            await EnsureGoogleInvariantAsync(userId, ct);
            changed = true;
        }

        if (command.Primary == UserEmailPrimaryChange.MakePrimary)
        {
            await SetPrimaryEmailAsync(userId, emailId, ct);
            changed = true;
        }
        else if (command.Primary == UserEmailPrimaryChange.ClearDuplicatePrimary)
        {
            changed |= await ClearDuplicatePrimaryAsync(userId, emailId, ct);
        }

        if (command.Google == UserEmailGoogleChange.MakeGoogle)
        {
            var row = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
            if (row is null || !row.IsVerified) return false;
            await repo.SetUserEmailGoogleExclusiveAsync(userId, emailId, clock.GetCurrentInstant(), ct);
            changed = true;
        }
        else if (command.Google == UserEmailGoogleChange.ClearDuplicateGoogle)
        {
            changed |= await ClearDuplicateGoogleAsync(userId, emailId, ct);
        }

        if (command.ChangeVisibility)
        {
            var row = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct)
                ?? throw new InvalidOperationException("Email not found.");
            row.Visibility = command.Visibility;
            row.UpdatedAt = clock.GetCurrentInstant();
            await repo.UpdateUserEmailAsync(row, ct);
            changed = true;
        }

        return changed;
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
            await EnsurePrimaryInvariantAsync(userId, ct);
            await EnsureGoogleInvariantAsync(userId, ct);
        }

        return true;
    }

    public async Task<UserEmailReconcilePlanResult> ApplyUserEmailReconcilePlanAsync(
        Guid userId,
        UserEmailReconcilePlanCommand command,
        CancellationToken ct = default)
    {
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
        CancellationToken ct = default)
    {
        var count = await repo.BackfillParticipationsAsync(year, entries, ct);
        logger.LogInformation(
            "Backfilled {Count} participation records for year {Year}",
            count, year);
        return count;
    }

    // --- IUserDataContributor — GDPR export ---

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return [
                new UserDataSlice(GdprExportSections.Account, null),
                new UserDataSlice(GdprExportSections.Profile, null)
            ];
        }

        // see nobodies-collective/Humans#687 — User.GoogleEmail deprecated, not exported.
        var userEmails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        var googleEmail = userEmails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault();
        var effectiveEmail = SelectEffectiveEmail(userEmails, user.IdentityEmailColumn);

#pragma warning disable HUM_USER_DISPLAYNAME // GDPR export must include the legacy Identity column value.
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
            DeletionRequestedAt = user.DeletionRequestedAt.ToInvariantInstantString(),
            DeletionScheduledFor = user.DeletionScheduledFor.ToInvariantInstantString(),
            CreatedAt = user.CreatedAt.ToInvariantInstantString(),
            LastLoginAt = user.LastLoginAt.ToInvariantInstantString()
        };
#pragma warning restore HUM_USER_DISPLAYNAME

        var participations = await repo.GetEventParticipationsByUserIdAsync(userId, ct);
        var participationsShaped = participations
            .Select(ep => new
            {
                ep.Year,
                Status = ep.Status.ToString(),
                Source = ep.Source.ToString(),
                DeclaredAt = ep.DeclaredAt.ToInvariantInstantString()
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
            ? new UserDataSlice(GdprExportSections.Profile, null)
            : new UserDataSlice(GdprExportSections.Profile, new
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
                profile.IsSuspended,
                profile.NoPriorBurnExperience,
                ConsentCheckStatus = profile.ConsentCheckStatus?.ToString(),
                ConsentCheckAt = profile.ConsentCheckAt.ToInvariantInstantString(),
                profile.ConsentCheckNotes,
                profile.RejectionReason,
                RejectedAt = profile.RejectedAt.ToInvariantInstantString(),
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
                CreatedAt = profile.CreatedAt.ToInvariantInstantString(),
                UpdatedAt = profile.UpdatedAt.ToInvariantInstantString()
            });

        var contactFieldSlice = new UserDataSlice(GdprExportSections.ContactFields, contactFields.Select(cf => new
        {
            cf.FieldType,
            Label = cf.DisplayLabel,
            cf.Value,
            cf.Visibility
        }).ToList());

        var userEmailsSlice = new UserDataSlice(GdprExportSections.UserEmails, userEmails.Select(e => new
        {
            e.Email,
            e.IsVerified,
            // JSON keys pinned per memory/code/no-rename-serialized-fields.md (GDPR export stability).
            IsOAuth = e.Provider != null,
            IsNotificationTarget = e.IsPrimary,
            e.Visibility
        }).ToList());

        var volunteerHistorySlice = new UserDataSlice(GdprExportSections.VolunteerHistory, volunteerHistory.Select(vh => new
        {
            Date = vh.Date.ToIsoDateString(),
            vh.EventName,
            vh.Description,
            CreatedAt = vh.CreatedAt.ToInvariantInstantString()
        }).ToList());

        var languagesSlice = new UserDataSlice(GdprExportSections.Languages, profileLanguages.Select(pl => new
        {
            pl.LanguageCode,
            pl.Proficiency
        }).ToList());

        var commPrefsSlice = new UserDataSlice(GdprExportSections.CommunicationPreferences, communicationPreferences.Select(cp => new
        {
            cp.Category,
            cp.OptedOut,
            cp.InboxEnabled,
            UpdatedAt = cp.UpdatedAt.ToInvariantInstantString(),
            cp.UpdateSource
        }).ToList());

        return
        [
            new UserDataSlice(GdprExportSections.Account, shaped),
            new UserDataSlice(GdprExportSections.EventParticipations, participationsShaped),
            profileSlice,
            contactFieldSlice,
            userEmailsSlice,
            volunteerHistorySlice,
            languagesSlice,
            commPrefsSlice
        ];
    }

    private static string? SelectEffectiveEmail(
        IReadOnlyCollection<UserEmail> userEmails,
        string? fallbackEmail) =>
        userEmails
            .Where(e => e.IsVerified)
            .OrderByDescending(e => e.IsPrimary)
            .Select(e => e.Email)
            .FirstOrDefault() ?? fallbackEmail;

    private async Task SetPrimaryEmailAsync(Guid userId, Guid emailId, CancellationToken ct)
    {
        var emails = (await repo.GetUserEmailsByUserIdForMutationAsync(userId, ct)).ToList();

        var target = emails.FirstOrDefault(e => e.Id == emailId)
            ?? throw new InvalidOperationException("Email not found.");

        if (!target.IsVerified)
            throw new ValidationException("Only verified emails can be the notification target.");

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

    private async Task AddRowWithInvariantsAsync(UserEmail row, CancellationToken ct)
    {
        await repo.AddUserEmailAsync(row, ct);
        await EnsurePrimaryInvariantAsync(row.UserId, ct);
        await EnsureGoogleInvariantAsync(row.UserId, ct);
    }

    private static void ApplyConsentCheck(
        Profile profile,
        UserProfileOnboardingCommand command,
        Instant now)
    {
        var cleared = command.ConsentCheckStatus == ConsentCheckStatus.Cleared;
        profile.ConsentCheckStatus = command.ConsentCheckStatus;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = command.ActorUserId;
        profile.ConsentCheckNotes = command.Notes;
        profile.IsApproved = cleared;
        profile.UpdatedAt = now;
    }

    private static void ApplySuspension(
        Profile profile,
        UserProfileOnboardingCommand command,
        Instant now)
    {
        var suspended = command.Suspended!.Value;

#pragma warning disable HUM_PROFILE_ISSUSPENDED
        profile.IsSuspended = suspended;
#pragma warning restore HUM_PROFILE_ISSUSPENDED

        // Dual-write until IsSuspended is dropped. State is the canonical shape
        // exposed through UserInfo.
        profile.State = suspended
            ? ProfileState.Suspended
            : HasRequiredNameFields(profile) ? ProfileState.Active : ProfileState.Stub;

        if (suspended)
            profile.AdminNotes = command.Notes;

        profile.UpdatedAt = now;
    }

    private static bool HasRequiredNameFields(Profile profile) =>
        !string.IsNullOrWhiteSpace(profile.BurnerName)
        && !string.IsNullOrWhiteSpace(profile.FirstName)
        && !string.IsNullOrWhiteSpace(profile.LastName);

    private static void ValidateProfileOnboardingCommand(UserProfileOnboardingCommand command)
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
    }

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
        await repo.ReassignSubAggregatesToUserAsync(mergedFromUserId, mergedToUserId, now, ct);
    }

    public Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(
        Guid targetUserId, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "GetMergedSourceIdsAsync is only meaningful through CachingUserService — " +
            "scans the cached UserInfo snapshot for MergedToUserId tombstones. If this is " +
            "being called on the inner UserService it indicates a DI registration mistake.");

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

    public Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default) =>
        repo.DeleteAllExternalLoginsForUserAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        repo.GetExternalLoginsByUserIdsAsync(userIds, ct);
}
