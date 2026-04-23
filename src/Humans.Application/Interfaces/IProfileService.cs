using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces;

public interface IProfileService
{
    Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the denormalized <see cref="FullProfile"/> projection for the
    /// given user, stitched from Profile + User + CV entries. The caching
    /// decorator serves dict hits synchronously; the base implementation loads
    /// from repositories each call.
    /// </summary>
    ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Batched profile fetch keyed by user id. Missing users are absent
    /// from the returned dictionary. Used by cross-section services that
    /// need to stitch profile slices in memory instead of pulling them
    /// through a cross-domain <c>.Include</c> chain.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the profile's <see cref="Profile.MembershipTier"/> and
    /// <see cref="Profile.UpdatedAt"/>, persists, and invalidates the
    /// profile's cache entry. No-op with a warning log if the user has no
    /// profile. Used by governance services that previously mutated the
    /// profile directly through a cross-domain navigation property.
    /// </summary>
    Task SetMembershipTierAsync(
        Guid userId,
        MembershipTier tier,
        CancellationToken ct = default);

    Task<(Profile? Profile, MemberApplication? LatestApplication, int PendingConsentCount)>
        GetProfileIndexDataAsync(Guid userId, CancellationToken ct = default);
    Task<(Profile? Profile, bool IsTierLocked, MemberApplication? PendingApplication)>
        GetProfileEditDataAsync(Guid userId, CancellationToken ct = default);
    Task<(byte[]? Data, string? ContentType)> GetProfilePictureAsync(Guid profileId, CancellationToken ct = default);
    Task<Guid> SaveProfileAsync(Guid userId, string displayName, ProfileSaveRequest request, string language, CancellationToken ct = default);
    Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default);
    Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a post-event hold date if the user has tickets for the active event,
    /// or null if no hold applies.
    /// </summary>
    Task<Instant?> GetEventHoldDateAsync(Guid userId, CancellationToken ct = default);
    Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default);

    Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the user ids of all profiles that are approved and not suspended.
    /// Used by cross-section notification fan-out (e.g. Legal document sync) that
    /// needs to target active members without reading the <c>profiles</c> table
    /// directly.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveApprovedUserIdsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedCampaignGrantsAsync(
        Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<DTOs.BirthdayProfileInfo>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default);

    Task<IReadOnlyList<DTOs.LocationProfileInfo>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DTOs.AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default);

    Task<DTOs.AdminHumanDetailData?> GetAdminHumanDetailAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<UserSearchResult>> SearchApprovedUsersAsync(string query, CancellationToken ct = default);

    Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Reconciles the user's CV entries (volunteer history) with the provided set.
    /// No-op if the user has no profile.
    /// </summary>
    Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Gets the languages associated with a profile, ordered by proficiency (descending) then language code.
    /// Returns an empty list if the profile does not exist.
    /// </summary>
    Task<IReadOnlyList<ProfileLanguage>> GetProfileLanguagesAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Replaces all languages for the given profile with the new set.
    /// </summary>
    Task SaveProfileLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default);

    // ==========================================================================
    // Onboarding-section support methods — exposed so OnboardingService can
    // coordinate profile mutations without touching the Profile section's
    // DbSet directly (design-rules §2c). Each method owns its own cache
    // invalidation (FullProfile refresh, nav-badge, notification meter) so the
    // Onboarding orchestrator has no cache responsibilities (§15i goal).
    // ==========================================================================

    /// <summary>
    /// Returns the review queue (profiles that are not approved and not
    /// rejected), ordered by creation time ascending. Used by the onboarding
    /// review queue for Consent Coordinators.
    /// </summary>
    Task<IReadOnlyList<Profile>> GetReviewableProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the count of profiles in the review queue (not approved, not
    /// rejected). Used by the nav badge for Consent Coordinators.
    /// </summary>
    Task<int> GetPendingReviewCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears the consent check for the given user (marks cleared + approved).
    /// Error keys: <c>NotFound</c>, <c>AlreadyRejected</c>.
    /// </summary>
    Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Flags the consent check for the given user (marks flagged + unapproved).
    /// Error keys: <c>NotFound</c>.
    /// </summary>
    Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Rejects a signup (records rejection reason, sets RejectedAt).
    /// Error keys: <c>NotFound</c>, <c>AlreadyRejected</c>.
    /// </summary>
    Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Approves a profile as volunteer (sets IsApproved).
    /// Error keys: <c>NotFound</c>.
    /// </summary>
    Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);

    /// <summary>
    /// Suspends the human (sets IsSuspended). Admin notes saved on the profile.
    /// Error keys: <c>NotFound</c>.
    /// </summary>
    Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Unsuspends the human (clears IsSuspended).
    /// Error keys: <c>NotFound</c>.
    /// </summary>
    Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);

    /// <summary>
    /// Sets a profile's consent check status to <c>Pending</c> and bumps
    /// <c>UpdatedAt</c>. Returns false if no profile exists. The caller is
    /// expected to have verified eligibility (all required consents signed,
    /// not approved, no existing status); this method performs the write and
    /// cache refresh.
    /// </summary>
    Task<bool> SetConsentCheckPendingAsync(Guid userId, CancellationToken ct = default);
}
