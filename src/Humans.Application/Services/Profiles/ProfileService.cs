using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Profiles;

namespace Humans.Application.Services.Profiles;

/// <summary>
/// Core profile service. Business logic only — no DbContext, no IMemoryCache.
/// Cache management is handled by the <c>CachingUserService</c> decorator.
/// Cross-domain reads use owning-section service interfaces.
/// </summary>
public sealed class ProfileService : IProfileService, IUserDataContributor, IUserMerge
{
    private readonly IProfileRepository _profileRepository;
    private readonly IUserService _userService;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IContactFieldRepository _contactFieldRepository;
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository;
    private readonly IOnboardingEligibilityQuery _onboardingEligibilityQuery;
    private readonly IAuditLogService _auditLogService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IFileStorage _fileStorage;
    private readonly IClock _clock;
    private readonly ILogger<ProfileService> _logger;

    // Per-user serialization for create-or-update on profiles.UserId. Single-server
    // deployment, so a process-local striped semaphore is sufficient — no Postgres
    // 23505 race can land if all writes for a given userId are sequentialized here.
    // Striped (32 buckets) so unrelated users never contend.
    private static readonly SemaphoreSlim[] _userLocks = CreateUserLocks(32);
    private static SemaphoreSlim[] CreateUserLocks(int count)
    {
        var locks = new SemaphoreSlim[count];
        for (var i = 0; i < count; i++) locks[i] = new SemaphoreSlim(1, 1);
        return locks;
    }
    private static SemaphoreSlim LockFor(Guid userId)
        => _userLocks[(uint)userId.GetHashCode() % (uint)_userLocks.Length];

    public ProfileService(
        IProfileRepository profileRepository,
        IUserService userService,
        IUserEmailRepository userEmailRepository,
        IContactFieldRepository contactFieldRepository,
        ICommunicationPreferenceRepository communicationPreferenceRepository,
        IOnboardingEligibilityQuery onboardingEligibilityQuery,
        IAuditLogService auditLogService,
        IMembershipCalculator membershipCalculator,
        IFileStorage fileStorage,
        IClock clock,
        ILogger<ProfileService> logger)
    {
        _profileRepository = profileRepository;
        _userService = userService;
        _userEmailRepository = userEmailRepository;
        _contactFieldRepository = contactFieldRepository;
        _communicationPreferenceRepository = communicationPreferenceRepository;
        _onboardingEligibilityQuery = onboardingEligibilityQuery;
        _auditLogService = auditLogService;
        _membershipCalculator = membershipCalculator;
        _fileStorage = fileStorage;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Domain.Entities.Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        return await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, Domain.Entities.Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        await _profileRepository.GetByUserIdsAsync(userIds, ct);

    public async Task SetMembershipTierAsync(
        Guid userId, MembershipTier tier, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
        {
            _logger.LogWarning(
                "Cannot set membership tier for user {UserId} — no profile exists", userId);
            return;
        }

        profile.MembershipTier = tier;
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _profileRepository.UpdateAsync(profile, ct);

        // Store update handled by CachingUserService decorator
    }

    public async Task EnsureStubProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var gate = LockFor(userId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await _profileRepository.GetByUserIdAsync(userId, ct);
            if (existing is not null) return;

            var now = _clock.GetCurrentInstant();
            var profile = new Domain.Entities.Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
                State = ProfileState.Stub,
            };
            await _profileRepository.AddAsync(profile, ct);
        }
        finally
        {
            gate.Release();
        }
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

        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
        {
            _logger.LogWarning(
                "Cannot set profile picture for user {UserId} — no profile exists", userId);
            return;
        }

        var oldContentType = profile.ProfilePictureContentType;
        profile.ProfilePictureData = pictureData;
        profile.ProfilePictureContentType = contentType;
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _profileRepository.UpdateAsync(profile, ct);

        try
        {
            // If the previous picture used a different content type (and
            // therefore a different extension on disk), remove the old file
            // so it doesn't linger orphaned.
            if (oldContentType is not null &&
                !string.Equals(oldContentType, contentType, StringComparison.Ordinal))
            {
                await _fileStorage.DeleteAsync(
                    ProfilePictureKey(profile.Id, oldContentType), ct);
            }
            await _fileStorage.SaveAsync(ProfilePictureKey(profile.Id, contentType), pictureData, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to write profile picture to filesystem for {ProfileId}; DB write still applied",
                profile.Id);
        }

        // UserInfo cache invalidation handled by CachingUserService decorator.
    }

    public async Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        // Anonymization gate (GDPR): a cheap scalar projection of the DB
        // content-type column is the source of truth for whether a picture
        // should be served. If anonymization (or any future cleanup) has
        // cleared the DB column, do not serve from disk even if a stale
        // file remains.
        var dbContentType = await _profileRepository.GetProfilePictureContentTypeAsync(profileId, ct);
        if (string.IsNullOrEmpty(dbContentType))
        {
            return null;
        }

        // Filesystem fast path. Avoids loading the bytea column when the file
        // is already on disk (the common case after migrate-on-read).
        var key = ProfilePictureKey(profileId, dbContentType);
        var fsBytes = await _fileStorage.TryReadAsync(key, ct);
        if (fsBytes is not null)
        {
            return (fsBytes, dbContentType);
        }

        // DB fallback + migrate-on-read.
        var (data, contentType) = await _profileRepository.GetProfilePictureDataAsync(profileId, ct);
        if (data is null || string.IsNullOrEmpty(contentType))
        {
            return null;
        }

        try
        {
            await _fileStorage.SaveAsync(ProfilePictureKey(profileId, contentType), data, ct);
            _logger.LogInformation(
                "Profile picture {ProfileId} served from DB fallback; migrated to filesystem",
                profileId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Profile picture {ProfileId} served from DB fallback; migration to filesystem failed",
                profileId);
        }

        return (data, contentType);
    }

    public async Task<ProfilePictureMigrationSnapshot> GetProfilePictureMigrationSnapshotAsync(
        CancellationToken ct = default)
    {
        var rows = await _profileRepository.GetCustomPictureRowsAsync(ct);
        if (rows.Count == 0)
        {
            return new ProfilePictureMigrationSnapshot(0, 0, Array.Empty<ProfilePictureMigrationRow>());
        }

        var users = await _userService.GetUserInfosAsync(rows.Select(r => r.UserId).ToList(), ct);

        var onFs = 0;
        var dbOnly = new List<ProfilePictureMigrationRow>();
        foreach (var (profileId, userId, burnerName, contentType, updatedAt) in rows)
        {
            var key = ProfilePictureKey(profileId, contentType);
            var bytes = await _fileStorage.TryReadAsync(key, ct);
            if (bytes is not null)
            {
                onFs++;
            }
            else
            {
                var displayName = !string.IsNullOrWhiteSpace(burnerName)
                    ? burnerName
                    : (users.TryGetValue(userId, out var u) ? u.DisplayName : string.Empty);
                dbOnly.Add(new ProfilePictureMigrationRow(profileId, userId, displayName, contentType, updatedAt));
            }
        }

        return new ProfilePictureMigrationSnapshot(rows.Count, onFs, dbOnly);
    }

    public async Task<Guid> SaveProfileAsync(
        Guid userId, string displayName, ProfileSaveRequest request, string language,
        CancellationToken ct = default)
    {
        // Serialize per-user so two concurrent first-time saves can't both pass
        // the GetByUserIdAsync null check and race on the profiles.UserId unique
        // index. Single-server deployment, so a process-local lock is sufficient.
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
        var now = _clock.GetCurrentInstant();

        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);

        if (profile is null)
        {
            // Issue #635 (§15i): newly created profiles always start as Stub.
            // Transitions to Active happen below once required fields are
            // populated (BurnerName/FirstName/LastName), giving the
            // ProfileService_UpdateProfileAsync_TransitionsStubToActive
            // behavior contract a single home.
            profile = new Domain.Entities.Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
                State = ProfileState.Stub,
            };
            await _profileRepository.AddAsync(profile, ct);
        }

        profile.BurnerName = request.BurnerName;
        profile.FirstName = request.FirstName;
        profile.LastName = request.LastName;
        profile.City = request.City;
        profile.CountryCode = request.CountryCode;
        profile.Latitude = request.Latitude;
        profile.Longitude = request.Longitude;
        profile.PlaceId = request.PlaceId;
        profile.Bio = request.Bio?.TrimEnd();
        profile.Pronouns = request.Pronouns;
        profile.ContributionInterests = request.ContributionInterests?.TrimEnd();
        profile.BoardNotes = request.BoardNotes?.TrimEnd();
        profile.EmergencyContactName = request.EmergencyContactName;
        profile.EmergencyContactPhone = request.EmergencyContactPhone;
        profile.EmergencyContactRelationship = request.EmergencyContactRelationship;
        profile.NoPriorBurnExperience = request.NoPriorBurnExperience;
        profile.UpdatedAt = now;

        // Parse birthday (stored as LocalDate with year=4 for Feb 29 validity)
        if (request.BirthdayMonth is >= 1 and <= 12 && request.BirthdayDay is >= 1 and <= 31)
        {
            try
            {
                profile.DateOfBirth = new LocalDate(4, request.BirthdayMonth.Value, request.BirthdayDay.Value);
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

        // Handle profile picture. Phase 1 of issue nobodies-collective/Humans#527:
        // dual-write to filesystem + DB so rollback stays safe. Phase 2 drops the
        // DB columns once phase 1 has bedded in.
        if (request.RemoveProfilePicture)
        {
            var oldContentType = profile.ProfilePictureContentType;
            profile.ProfilePictureData = null;
            profile.ProfilePictureContentType = null;
            if (oldContentType is not null)
            {
                try
                {
                    await _fileStorage.DeleteAsync(ProfilePictureKey(profile.Id, oldContentType), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Failed to delete profile picture from filesystem for {ProfileId}; DB delete still applied",
                        profile.Id);
                }
            }
        }
        else if (request.ProfilePictureData is not null && request.ProfilePictureContentType is not null)
        {
            var oldContentType = profile.ProfilePictureContentType;
            profile.ProfilePictureData = request.ProfilePictureData;
            profile.ProfilePictureContentType = request.ProfilePictureContentType;
            try
            {
                // If the previous picture used a different content type (and
                // therefore a different on-disk extension), remove the old
                // file so it doesn't linger orphaned.
                if (oldContentType is not null &&
                    !string.Equals(oldContentType, request.ProfilePictureContentType, StringComparison.Ordinal))
                {
                    await _fileStorage.DeleteAsync(ProfilePictureKey(profile.Id, oldContentType), ct);
                }
                await _fileStorage.SaveAsync(
                    ProfilePictureKey(profile.Id, request.ProfilePictureContentType),
                    request.ProfilePictureData,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to write profile picture to filesystem for {ProfileId}; DB write still applied",
                    profile.Id);
            }
        }

        // Issue #635 (§15i): Stub → Active transition. When all required
        // identity fields are populated and the profile is not Suspended,
        // promote the lifecycle marker. Predicate lives on the Profile
        // entity so the same rule serves the lazy-compute path in
        // CachingUserService.ComputeProfileState.
        if (profile.State != ProfileState.Suspended)
        {
            profile.State = profile.HasRequiredIdentityFields()
                ? ProfileState.Active
                : ProfileState.Stub;
        }

        await _profileRepository.UpdateAsync(profile, ct);

        // Update display name on user (cross-section → IUserService)
        await _userService.UpdateDisplayNameAsync(userId, displayName, ct);

        // Cache invalidation and store update handled by CachingUserService decorator

        // Check consent eligibility
        await _onboardingEligibilityQuery.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        _logger.LogInformation("User {UserId} updated their profile", userId);

        return profile.Id;
    }

    public Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default) =>
        _profileRepository.GetCustomPictureInfoByUserIdsAsync(userIds, ct);

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var pendingRecord = await _userEmailRepository.GetByIdReadOnlyAsync(pendingEmailId, ct);

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
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null) return;

        await _profileRepository.ReconcileCVEntriesAsync(profile.Id, entries, ct);
    }

    public async Task<IReadOnlyList<ProfileLanguageSnapshot>> GetProfileLanguagesAsync(
        Guid profileId, CancellationToken ct = default)
    {
        var languages = await _profileRepository.GetLanguagesAsync(profileId, ct);
        return languages.Select(l => new ProfileLanguageSnapshot(
            l.Id,
            l.ProfileId,
            l.LanguageCode,
            l.Proficiency)).ToList();
    }

    public Task SaveProfileLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default) =>
        _profileRepository.ReplaceLanguagesAsync(profileId, languages, ct);

    // ==========================================================================
    // Volunteer Event Profiles — cross-section reads (§15 Step 1 quarantine)
    // ==========================================================================
    // GDPR Export — contributes Profile-section slices
    // ==========================================================================

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);

        var contactFields = profile is not null
            ? await _contactFieldRepository.GetByProfileIdReadOnlyAsync(profile.Id, ct)
            : [];

        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);

        // VolunteerHistory is eagerly loaded by GetByUserIdReadOnlyAsync
        var volunteerHistory = profile?.VolunteerHistory
            .OrderByDescending(v => v.Date)
            .ThenByDescending(v => v.CreatedAt)
            .ToList() ?? (IReadOnlyList<VolunteerHistoryEntry>)[];

        var profileLanguages = profile is not null
            ? await _profileRepository.GetLanguagesAsync(profile.Id, ct)
            : [];

        var communicationPreferences = await _communicationPreferenceRepository
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
            // JSON keys stay "IsOAuth" and "IsNotificationTarget" per
            // memory/code/no-rename-serialized-fields.md — the GDPR export is a JSON
            // file users download. IsOAuth sources from (Provider != null) — the
            // pre-PR-4 semantics meaning "this row has an OAuth login attached".
            // The PR 4 spec's Task 17 swapped both the JSON key (rename) and the
            // value source (e.IsGoogle); both have been reverted so the export
            // emits identical bytes for the same row data as before PR 4.
            // IsNotificationTarget is the legacy JSON key for the renamed C#
            // property IsPrimary (mirrors the EF HasColumnName pin).
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

    // ==========================================================================
    // Onboarding-section support methods — profile mutations that OnboardingService
    // delegates here so each section owns its DbSet writes (design-rules §2c).
    // Cache invalidation (UserInfo refresh, nav-badge, notification meter) is
    // handled by the CachingUserService decorator's wrappers for these methods.
    // ==========================================================================

    public async Task<OnboardingResult> RecordConsentCheckAsync(
        Guid userId, Guid reviewerId, ConsentCheckStatus result, string? notes,
        CancellationToken ct = default)
    {
        if (result is not ConsentCheckStatus.Cleared and not ConsentCheckStatus.Flagged)
        {
            throw new ArgumentException(
                $"RecordConsentCheckAsync only accepts Cleared or Flagged; use SetConsentCheckPendingAsync for the system-driven Pending transition.",
                nameof(result));
        }

        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        var cleared = result == ConsentCheckStatus.Cleared;

        if (cleared && profile.RejectedAt is not null)
            return new OnboardingResult(false, "AlreadyRejected");

        var now = _clock.GetCurrentInstant();

        profile.ConsentCheckStatus = result;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = reviewerId;
        profile.ConsentCheckNotes = notes;
        profile.IsApproved = cleared;
        profile.UpdatedAt = now;

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            cleared ? AuditAction.ConsentCheckCleared : AuditAction.ConsentCheckFlagged,
            nameof(Domain.Entities.Profile), userId,
            cleared ? "Consent check cleared" : $"Consent check flagged: {notes}",
            reviewerId);

        _logger.LogInformation(
            "Consent check {Status} for user {UserId} by {ReviewerId}",
            cleared ? "cleared" : "flagged", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        if (profile.RejectedAt is not null)
            return new OnboardingResult(false, "AlreadyRejected");

        var now = _clock.GetCurrentInstant();

        profile.RejectionReason = reason;
        profile.RejectedAt = now;
        profile.RejectedByUserId = reviewerId;
        profile.IsApproved = false;
        profile.UpdatedAt = now;

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            AuditAction.SignupRejected, nameof(Domain.Entities.Profile), userId,
            $"Signup rejected{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            reviewerId);

        _logger.LogInformation("Signup rejected for user {UserId} by {ReviewerId}", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        var now = _clock.GetCurrentInstant();

        profile.IsApproved = true;
        profile.UpdatedAt = now;

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            AuditAction.VolunteerApproved, nameof(User), userId,
            "Approved as volunteer",
            adminId);

        _logger.LogInformation("Admin {AdminId} approved human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> SetSuspendedAsync(
        Guid userId, Guid adminId, bool suspended, string? notes,
        CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

#pragma warning disable HUM_PROFILE_ISSUSPENDED
        profile.IsSuspended = suspended;
#pragma warning restore HUM_PROFILE_ISSUSPENDED

        // Issue #635 (§15i): mirror the bool into ProfileState. New write paths
        // go through State; the bool write above is kept dual until the
        // separate follow-up PR drops the column after prod soak.
        if (suspended)
        {
            profile.State = ProfileState.Suspended;
        }
        else
        {
            profile.State = profile.HasRequiredIdentityFields()
                ? ProfileState.Active
                : ProfileState.Stub;
        }

        if (suspended)
            profile.AdminNotes = notes;
        profile.UpdatedAt = _clock.GetCurrentInstant();

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            suspended ? AuditAction.MemberSuspended : AuditAction.MemberUnsuspended,
            nameof(User), userId,
            suspended
                ? $"Suspended{(string.IsNullOrWhiteSpace(notes) ? "" : $": {notes}")}"
                : "Unsuspended",
            adminId);

        _logger.LogInformation(
            "Admin {AdminId} {Verb} human {HumanId}",
            adminId, suspended ? "suspended" : "unsuspended", userId);

        return new OnboardingResult(true);
    }

    public async Task<bool> SetConsentCheckPendingAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return false;

        profile.ConsentCheckStatus = ConsentCheckStatus.Pending;
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _profileRepository.UpdateAsync(profile, ct);

        _logger.LogInformation(
            "User {UserId} has all consents signed, consent check set to Pending", userId);

        return true;
    }

    public async Task<bool> AnonymizeExpiredProfileAsync(Guid userId, CancellationToken ct = default)
    {
        // Anonymize clears ProfilePictureData / ProfilePictureContentType in
        // the DB and best-effort wipes the filesystem copy (phase 1 of issue
        // nobodies-collective/Humans#527). The DB clear alone is NOT
        // sufficient under the FS-first read path: if this delete throws,
        // the file remains on disk and TryReadAsync would otherwise serve it
        // indefinitely. The read-path gate in GetProfilePictureAsync (which
        // checks the DB content-type before consulting the filesystem)
        // closes that loop — but we still log this failure as an Error so
        // an operator can clean up the stale file out-of-band.
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
        var anonymized = await _profileRepository.AnonymizeForDeletionByUserIdAsync(userId, ct);
        if (anonymized && profile?.ProfilePictureContentType is not null)
        {
            try
            {
                await _fileStorage.DeleteAsync(
                    ProfilePictureKey(profile.Id, profile.ProfilePictureContentType), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Failed to delete filesystem profile picture during anonymization for {ProfileId}; " +
                    "DB has been cleared so the read-path gate prevents the stale file from being served, " +
                    "but the file should be removed manually to complete GDPR data deletion",
                    profile.Id);
            }
        }
        return anonymized;
    }

    // ==========================================================================
    // Profile picture filesystem helpers
    // ==========================================================================

    /// <summary>
    /// Returns the IFileStorage key for a profile picture with the given
    /// content type. Profile pictures live under <c>uploads/profile-pictures/</c>
    /// but are NOT publicly served — Program.cs configures static-file
    /// middleware to 404 that subpath so reads must go through
    /// <see cref="GetProfilePictureAsync"/> (which applies the GDPR gate).
    /// The extension is derived from the content type (empty string for
    /// unknown types — file lives at the bare profile id).
    /// </summary>
    internal static string ProfilePictureKey(Guid profileId, string contentType) =>
        $"uploads/profile-pictures/{profileId}{ExtensionFromContentType(contentType)}";

    internal static string ExtensionFromContentType(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => string.Empty
    };

    public Task<IReadOnlySet<Guid>> SuspendForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        Instant now,
        CancellationToken ct = default) =>
        _profileRepository.SuspendManyAsync(userIds, now, ct);

    public Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default) =>
        _profileRepository.DowngradeTierForExpiredAsync(
            currentTier, userIdsToKeep, fallbackTierByUser, now, ct);

    // Cache invalidation (UserInfo refresh for both source and target) is
    // handled by the CachingUserService decorator's wrapper for this
    // method — ProfileService is the inner / non-cached implementation.
    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct) =>
        _profileRepository.ReassignSubAggregatesToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

    public async Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return false;

        // Use IbanValidator.Normalize so the persisted value matches what IsValid accepts —
        // the validator strips both U+0020 and U+202F (narrow no-break space, common in
        // bank-PDF copy-paste). A mismatched normalizer here lets hidden whitespace ride
        // into SEPA/Holded payloads and downstream payment rejections.
        var normalized = string.IsNullOrWhiteSpace(iban) ? null : IbanValidator.Normalize(iban);
        var isClearing = normalized is null;

        profile.Iban = normalized;
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            isClearing ? AuditAction.IbanRemove : AuditAction.IbanSet,
            nameof(Domain.Entities.Profile), userId,
            isClearing ? "IBAN removed" : "IBAN set",
            userId);

        _logger.LogInformation(
            "IBAN {Action} for user {UserId}", isClearing ? "removed" : "set", userId);

        return true;
    }

}
