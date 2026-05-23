using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Profiles;

public sealed class ProfileService(IProfileRepository profileRepository,
    IUserService userService,
    IUserEmailRepository userEmailRepository,
    IContactFieldRepository contactFieldRepository,
    ICommunicationPreferenceRepository communicationPreferenceRepository,
    IAuditLogService auditLogService,
    IFileStorage fileStorage,
    IClock clock,
    ILogger<ProfileService> logger) : IProfileService, IUserDataContributor, IUserMerge
{
    // Striped process-local semaphore serializes create-or-update per userId (single-server; avoids profiles.UserId 23505 race).
    private static readonly SemaphoreSlim[] _userLocks = CreateUserLocks(32);
    private static SemaphoreSlim[] CreateUserLocks(int count)
    {
        var locks = new SemaphoreSlim[count];
        for (var i = 0; i < count; i++) locks[i] = new SemaphoreSlim(1, 1);
        return locks;
    }
    private static SemaphoreSlim LockFor(Guid userId)
        => _userLocks[(uint)userId.GetHashCode() % (uint)_userLocks.Length];

    public async Task SetMembershipTierAsync(
        Guid userId, MembershipTier tier, CancellationToken ct = default)
    {
        await userService.SetMembershipTierAsync(userId, tier, ct);
    }

    public async Task EnsureStubProfileAsync(Guid userId, CancellationToken ct = default)
    {
        await userService.EnsureStubProfileAsync(userId, ct);
    }

    public async Task SetProfilePictureAsync(
        Guid userId, byte[] pictureData, string contentType, CancellationToken ct = default)
    {
        if (pictureData.Length == 0)
        {
            throw new ArgumentException("Picture data must not be empty", nameof(pictureData));
        }
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("Content type must not be empty", nameof(contentType));
        }

        var storageResult = await userService.SetProfilePictureContentTypeAsync(userId, contentType, ct);
        if (!storageResult.Saved || storageResult.ProfileId is null)
        {
            logger.LogWarning(
                "Cannot set profile picture for user {UserId} - no profile exists", userId);
            return;
        }

        try
        {
            // Remove old file if content-type/extension changed.
            if (storageResult.PreviousProfilePictureContentType is not null &&
                !string.Equals(storageResult.PreviousProfilePictureContentType, contentType, StringComparison.Ordinal))
            {
                await fileStorage.DeleteAsync(
                    ProfilePictureKey(storageResult.ProfileId.Value, storageResult.PreviousProfilePictureContentType), ct);
            }
            await fileStorage.SaveAsync(ProfilePictureKey(storageResult.ProfileId.Value, contentType), pictureData, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to write profile picture to filesystem for {ProfileId}; content-type column is set but the file is missing - picture will not render",
                storageResult.ProfileId.Value);
        }

    }

    public async Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        // GDPR gate: content-type column is the source of truth — don't serve from disk if cleared.
        var dbContentType = await profileRepository.GetProfilePictureContentTypeAsync(profileId, ct);
        if (string.IsNullOrEmpty(dbContentType))
        {
            return null;
        }

        var key = ProfilePictureKey(profileId, dbContentType);
        var fsBytes = await fileStorage.TryReadAsync(key, ct);
        return fsBytes is not null ? (fsBytes, dbContentType) : null;
    }

    public async Task<ProfilePictureMigrationSnapshot> GetProfilePictureMigrationSnapshotAsync(
        CancellationToken ct = default)
    {
        var rows = await profileRepository.GetCustomPictureRowsAsync(ct);
        if (rows.Count == 0)
        {
            return new ProfilePictureMigrationSnapshot(0, 0, []);
        }

        var users = await userService.GetUserInfosAsync(rows.Select(r => r.UserId).ToList(), ct);

        var onFs = 0;
        var dbOnly = new List<ProfilePictureMigrationRow>();
        foreach (var (profileId, userId, burnerName, contentType, updatedAt) in rows)
        {
            var key = ProfilePictureKey(profileId, contentType);
            var bytes = await fileStorage.TryReadAsync(key, ct);
            if (bytes is not null)
            {
                onFs++;
            }
            else
            {
                var displayName = !string.IsNullOrWhiteSpace(burnerName)
                    ? burnerName
                    : (users.TryGetValue(userId, out var u) ? u.BurnerName : string.Empty);
                dbOnly.Add(new ProfilePictureMigrationRow(profileId, userId, displayName, contentType, updatedAt));
            }
        }

        return new ProfilePictureMigrationSnapshot(rows.Count, onFs, dbOnly);
    }

    public async Task<Guid> SaveProfileAsync(
        Guid userId, string displayName, ProfileSaveRequest request, string language,
        CancellationToken ct = default)
    {
        // Serialize full save orchestration so DB picture metadata and file writes stay ordered per user.
        var gate = LockFor(userId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SaveProfileCoreAsync(userId, displayName, request, language, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Guid> SaveProfileCoreAsync(
        Guid userId, string displayName, ProfileSaveRequest request, string language,
        CancellationToken ct)
    {
        var storageResult = await userService.SaveProfileAsync(
            userId,
            ToUserProfileSaveCommand(displayName, request),
            ct);

        await ApplyProfilePictureFileMutationAsync(storageResult, request, ct);

        logger.LogInformation("User {UserId} updated their profile", userId);

        return storageResult.ProfileId;
    }

    private static UserProfileSaveCommand ToUserProfileSaveCommand(
        string displayName,
        ProfileSaveRequest request)
    {
        var pictureMutation = request.RemoveProfilePicture
            ? UserProfilePictureMutation.Remove
            : request.ProfilePictureData is not null && request.ProfilePictureContentType is not null
                ? UserProfilePictureMutation.Set
                : UserProfilePictureMutation.None;

        return new UserProfileSaveCommand(
            DisplayName: displayName,
            BurnerName: request.BurnerName,
            FirstName: request.FirstName,
            LastName: request.LastName,
            City: request.City,
            CountryCode: request.CountryCode,
            Latitude: request.Latitude,
            Longitude: request.Longitude,
            PlaceId: request.PlaceId,
            Bio: request.Bio,
            Pronouns: request.Pronouns,
            ContributionInterests: request.ContributionInterests,
            BoardNotes: request.BoardNotes,
            BirthdayMonth: request.BirthdayMonth,
            BirthdayDay: request.BirthdayDay,
            EmergencyContactName: request.EmergencyContactName,
            EmergencyContactPhone: request.EmergencyContactPhone,
            EmergencyContactRelationship: request.EmergencyContactRelationship,
            NoPriorBurnExperience: request.NoPriorBurnExperience,
            PictureMutation: pictureMutation,
            ProfilePictureContentType: request.ProfilePictureContentType);
    }

    private async Task ApplyProfilePictureFileMutationAsync(
        UserProfileSaveResult storageResult,
        ProfileSaveRequest request,
        CancellationToken ct)
    {
        if (request.RemoveProfilePicture)
        {
            if (storageResult.PreviousProfilePictureContentType is not null)
            {
                try
                {
                    await fileStorage.DeleteAsync(
                        ProfilePictureKey(storageResult.ProfileId, storageResult.PreviousProfilePictureContentType),
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Failed to delete profile picture from filesystem for {ProfileId}; content-type column has been cleared so the file will not be served",
                        storageResult.ProfileId);
                }
            }

            return;
        }

        if (request.ProfilePictureData is null || request.ProfilePictureContentType is null)
            return;

        try
        {
            if (storageResult.PreviousProfilePictureContentType is not null &&
                !string.Equals(storageResult.PreviousProfilePictureContentType, request.ProfilePictureContentType, StringComparison.Ordinal))
            {
                await fileStorage.DeleteAsync(
                    ProfilePictureKey(storageResult.ProfileId, storageResult.PreviousProfilePictureContentType),
                    ct);
            }

            await fileStorage.SaveAsync(
                ProfilePictureKey(storageResult.ProfileId, request.ProfilePictureContentType),
                request.ProfilePictureData,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to write profile picture to filesystem for {ProfileId}; content-type column is set but the file is missing - picture will not render",
                storageResult.ProfileId);
        }
    }

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var pendingRecord = await userEmailRepository.GetByIdReadOnlyAsync(pendingEmailId, ct);

        if (pendingRecord?.VerificationSentAt.HasValue == true)
        {
            var cooldownEnd = pendingRecord.VerificationSentAt.Value.Plus(Duration.FromMinutes(5));
            if (now < cooldownEnd)
            {
                var minutesUntilResend = (int)Math.Ceiling((cooldownEnd - now).TotalMinutes);
                return (false, minutesUntilResend, pendingEmailId);
            }
        }

        return (true, 0, null);
    }

    public async Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default)
    {
        await userService.SaveProfileVolunteerHistoryAsync(userId, entries, ct);
    }

    public async Task<IReadOnlyList<ProfileLanguageSnapshot>> GetProfileLanguagesAsync(
        Guid profileId, CancellationToken ct = default)
    {
        var languages = await profileRepository.GetLanguagesAsync(profileId, ct);
        return languages.Select(l => new ProfileLanguageSnapshot(
            l.Id,
            l.ProfileId,
            l.LanguageCode,
            l.Proficiency)).ToList();
    }

    public async Task SaveProfileLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default)
    {
        await userService.SaveProfileLanguagesAsync(profileId, languages, ct);
    }

    // --- GDPR Export — Profile-section slices ---

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var profile = await profileRepository.GetByUserIdReadOnlyAsync(userId, ct);

        var contactFields = profile is not null
            ? await contactFieldRepository.GetByProfileIdReadOnlyAsync(profile.Id, ct)
            : [];

        var userEmails = await userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);

        var volunteerHistory = profile?.VolunteerHistory
            .OrderByDescending(v => v.Date)
            .ThenByDescending(v => v.CreatedAt)
            .ToList() ?? (IReadOnlyList<VolunteerHistoryEntry>)[];

        var profileLanguages = profile is not null
            ? await profileRepository.GetLanguagesAsync(profile.Id, ct)
            : [];

        var communicationPreferences = await communicationPreferenceRepository
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

        return [
            profileSlice,
            contactFieldSlice,
            userEmailsSlice,
            volunteerHistorySlice,
            languagesSlice,
            commPrefsSlice
        ];
    }

    // --- Onboarding support — profile mutations OnboardingService delegates here (§2c) ---

    public async Task<OnboardingResult> RecordConsentCheckAsync(
        Guid userId, Guid reviewerId, ConsentCheckStatus result, string? notes,
        CancellationToken ct = default)
    {
        var storageResult = await userService.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.RecordConsentCheck,
                ActorUserId: reviewerId,
                ConsentCheckStatus: result,
                Notes: notes),
            ct);

        if (!storageResult.Success)
            return storageResult;

        var cleared = result == ConsentCheckStatus.Cleared;
        await auditLogService.LogAsync(
            cleared ? AuditAction.ConsentCheckCleared : AuditAction.ConsentCheckFlagged,
            nameof(Profile), userId,
            cleared ? "Consent check cleared" : $"Consent check flagged: {notes}",
            reviewerId);

        logger.LogInformation(
            "Consent check {Status} for user {UserId} by {ReviewerId}",
            cleared ? "cleared" : "flagged", userId, reviewerId);

        return storageResult;
    }

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        var storageResult = await userService.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.RejectSignup,
                ActorUserId: reviewerId,
                RejectionReason: reason),
            ct);

        if (!storageResult.Success)
            return storageResult;

        await auditLogService.LogAsync(
            AuditAction.SignupRejected, nameof(Profile), userId,
            $"Signup rejected{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            reviewerId);

        logger.LogInformation("Signup rejected for user {UserId} by {ReviewerId}", userId, reviewerId);

        return storageResult;
    }

    public async Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var storageResult = await userService.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.ApproveVolunteer,
                ActorUserId: adminId),
            ct);

        if (!storageResult.Success)
            return storageResult;

        await auditLogService.LogAsync(
            AuditAction.VolunteerApproved, nameof(User), userId,
            "Approved as volunteer",
            adminId);

        logger.LogInformation("Admin {AdminId} approved human {HumanId}", adminId, userId);

        return storageResult;
    }

    public async Task<OnboardingResult> SetSuspendedAsync(
        Guid userId, Guid adminId, bool suspended, string? notes,
        CancellationToken ct = default)
    {
        var storageResult = await userService.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.SetSuspension,
                ActorUserId: adminId,
                Notes: notes,
                Suspended: suspended),
            ct);

        if (!storageResult.Success)
            return storageResult;

        await auditLogService.LogAsync(
            suspended ? AuditAction.MemberSuspended : AuditAction.MemberUnsuspended,
            nameof(User), userId,
            suspended
                ? $"Suspended{(string.IsNullOrWhiteSpace(notes) ? "" : $": {notes}")}"
                : "Unsuspended",
            adminId);

        logger.LogInformation(
            "Admin {AdminId} {Verb} human {HumanId}",
            adminId, suspended ? "suspended" : "unsuspended", userId);

        return storageResult;
    }

    public async Task<bool> SetConsentCheckPendingAsync(Guid userId, CancellationToken ct = default)
    {
        var storageResult = await userService.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(UserProfileOnboardingMutation.SetConsentCheckPending),
            ct);

        if (!storageResult.Success)
            return false;

        logger.LogInformation(
            "User {UserId} has all consents signed, consent check set to Pending", userId);

        return true;
    }

    public async Task<bool> AnonymizeExpiredProfileAsync(Guid userId, CancellationToken ct = default)
    {
        // Clear content-type column (GDPR read-gate) then best-effort delete file; log on FS failure for manual cleanup.
        var storageResult = await userService.AnonymizeProfileForDeletionAsync(userId, ct);
        if (storageResult.Anonymized &&
            storageResult.ProfileId is { } profileId &&
            storageResult.PreviousProfilePictureContentType is { } contentType)
        {
            try
            {
                await fileStorage.DeleteAsync(
                    ProfilePictureKey(profileId, contentType), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Failed to delete filesystem profile picture during anonymization for {ProfileId}; " +
                    "DB has been cleared so the read-path gate prevents the stale file from being served, " +
                    "but the file should be removed manually to complete GDPR data deletion",
                    profileId);
            }
        }
        return storageResult.Anonymized;
    }

    // Pictures live at uploads/profile-pictures/{id}{ext}; Program.cs 404s the subpath so reads go through GetProfilePictureAsync (GDPR gate).
    internal static string ProfilePictureKey(Guid profileId, string contentType) =>
        $"uploads/profile-pictures/{profileId}{ExtensionFromContentType(contentType)}";

    internal static string ExtensionFromContentType(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => string.Empty
    };

    public async Task<IReadOnlySet<Guid>> SuspendForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        Instant now,
        CancellationToken ct = default)
    {
        return await userService.SuspendProfilesForMissingConsentAsync(userIds, now, ct);
    }

    public async Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default)
    {
        return await userService.DowngradeMembershipTierForExpiredAsync(
            currentTier, userIdsToKeep, fallbackTierByUser, now, ct);
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await userService.ReassignProfileSubAggregatesAsync(sourceUserId, targetUserId, updatedAt, ct);
    }

    public async Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(iban) ? null : IbanValidator.Normalize(iban);
        var isClearing = normalized is null;

        var saved = await userService.SetProfileIbanAsync(userId, normalized, ct);
        if (!saved)
            return false;

        await auditLogService.LogAsync(
            isClearing ? AuditAction.IbanRemove : AuditAction.IbanSet,
            nameof(Profile), userId,
            isClearing ? "IBAN removed" : "IBAN set",
            userId);

        logger.LogInformation(
            "IBAN {Action} for user {UserId}", isClearing ? "removed" : "set", userId);

        return true;
    }

}
