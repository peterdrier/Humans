using Humans.Application.DTOs;
using Humans.Application.Interfaces.Onboarding;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces.Profiles;

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

    /// <summary>
    /// Returns the count of profiles whose <c>ConsentCheckStatus</c> is Pending
    /// or Flagged and whose <c>RejectedAt</c> is null. Used by the notification
    /// meter to surface pending consent reviews to Consent Coordinators
    /// without letting the Notifications section read the <c>profiles</c> table
    /// directly (design-rules §2c).
    /// </summary>
    Task<int> GetConsentReviewPendingCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the count of profiles that are neither approved nor suspended.
    /// Used by the notification meter to compute the "onboarding profiles
    /// pending" queue (total-not-approved minus consent reviews pending).
    /// </summary>
    Task<int> GetNotApprovedAndNotSuspendedCountAsync(CancellationToken ct = default);

    Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedCampaignGrantsAsync(
        Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<BirthdayProfileInfo>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default);

    Task<IReadOnlyList<LocationProfileInfo>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default);

    Task<AdminHumanDetailData?> GetAdminHumanDetailAsync(Guid userId, CancellationToken ct = default);

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
    /// Auto-clear a pending consent check on behalf of the system (no human reviewer).
    /// Mirrors <see cref="ClearConsentCheckAsync"/> in side effects (IsApproved=true,
    /// status=Cleared) but leaves <c>ConsentCheckedByUserId</c> null and audits as
    /// <see cref="Humans.Domain.Enums.AuditAction.ConsentCheckAutoCleared"/> with
    /// <paramref name="actorName"/> as the actor. Only succeeds when the profile
    /// is currently in <see cref="ConsentCheckStatus.Pending"/>.
    /// Error keys: <c>NotFound</c>, <c>AlreadyRejected</c>, <c>NotPending</c>.
    /// </summary>
    Task<OnboardingResult> AutoClearConsentCheckAsync(
        Guid userId, string reason, string modelId, string actorName, CancellationToken ct = default);

    /// <summary>
    /// User ids of profiles currently sitting in the Consent Check = Pending
    /// bucket (not yet cleared, not flagged, not rejected). Used by
    /// <c>AutoConsentCheckJob</c> to enumerate the queue.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetPendingConsentCheckUserIdsAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Anonymizes the personal fields of the user's profile for GDPR
    /// expiry-based deletion: clears first/last name → "Deleted"/"User",
    /// burner name → empty, and blanks every optional demographic /
    /// emergency-contact / admin-note / contribution-interest field. Also
    /// removes every <c>ContactField</c> and <c>VolunteerHistoryEntry</c> row
    /// owned by the profile in the same save. No-op if the user has no
    /// profile. Returns <c>true</c> if a profile was anonymized. Used by the
    /// account deletion job via
    /// <see cref="Users.IUserService.AnonymizeExpiredAccountAsync"/>.
    /// </summary>
    Task<bool> AnonymizeExpiredProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="Profile.IsSuspended"/> to true and stamps
    /// <see cref="Profile.UpdatedAt"/> for users whose consent grace period has
    /// expired. Unlike <see cref="SuspendAsync"/>, this variant does not
    /// require an admin actor, skip-list already-suspended profiles (so the
    /// caller can pre-filter with the returned set), and does not write an
    /// audit log entry — the caller is expected to emit the
    /// <see cref="Humans.Domain.Enums.AuditAction.MemberSuspended"/> entry
    /// itself so it can include job-specific context.
    /// Returns the set of user ids whose profile was actually mutated (i.e.
    /// those who had a profile and were not already suspended).
    /// No-op (absent from the returned set) for users without a profile or
    /// already suspended. Used by the SuspendNonCompliantMembersJob so the
    /// Profile section owns the write (design-rules §2c).
    /// </summary>
    Task<IReadOnlySet<Guid>> SuspendForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// For every profile whose <see cref="Profile.MembershipTier"/> equals
    /// <paramref name="currentTier"/> and whose <c>UserId</c> is NOT in
    /// <paramref name="userIdsToKeep"/>, downgrade the tier to the value
    /// supplied by <paramref name="fallbackTierByUser"/> (falling back to
    /// <see cref="MembershipTier.Volunteer"/> when the user is absent from
    /// the map). Stamps <see cref="Profile.UpdatedAt"/> to
    /// <paramref name="now"/> and persists in a single save. Returns a list
    /// of (UserId, NewTier) tuples so the caller can emit audit entries
    /// without a second round-trip. Used by
    /// <c>SystemTeamSyncJob.SyncTierTeamAsync</c> so the job does not write
    /// to <c>profiles</c> directly (design-rules §2c).
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default);
}
