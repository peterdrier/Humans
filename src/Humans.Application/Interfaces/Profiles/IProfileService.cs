using Humans.Application.Architecture;
using Humans.Application.Interfaces;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Service for managing profile data and profile-side merge fan-out.
/// </summary>
/// <remarks>
/// Surface-budget recent history (newest first, last 3 only — see memory/code/surface-budget-history-trim.md):
/// <list type="bullet">
///   <item>27→24 — PR #553 follow-up: dropped 3 cross-section accessors (GetConsentReviewPendingCountAsync, GetNotApprovedAndNotSuspendedCountAsync, GetActiveApprovedUserIdsAsync). Callers now read UserInfo via IUserService.</item>
///   <item>32→27 — PR #553: drained 5 single-caller DB readers + reimplemented 3 multi-caller readers over the cached UserInfo snapshot. ReviewQueueData retyped to carry UserInfo.</item>
///   <item>2026-05-13 — 31→32 (issue #702): added GetProfilePictureMigrationSnapshotAsync for the DB→FS picture migration verification page.</item>
/// </list>
/// </remarks>
[SurfaceBudget(24)]
public interface IProfileService : IApplicationService, IUserMerge
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
    /// Issue #635 (§15i): idempotently materialize a <see cref="ProfileState.Stub"/>
    /// Profile row for the given user. Called by User-creation paths
    /// (<c>AccountController.ExternalLoginCallback</c>, <c>AccountController.CompleteSignup</c>,
    /// <c>AccountProvisioningService.FindOrCreateUserByEmailAsync</c>) so cross-
    /// section reads can rely on a non-null Profile pointer. No-op if a profile
    /// already exists. The caching decorator refreshes the FullProfile entry
    /// after the write so downstream reads see the new Stub immediately.
    /// </summary>
    Task EnsureStubProfileAsync(Guid userId, CancellationToken ct = default);

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

    /// <summary>
    /// Returns the profile picture for the given profile, reading from the
    /// filesystem store first and falling back to the DB column. On a
    /// DB-fallback hit the bytes are migrated to the filesystem store so
    /// subsequent requests use the fast path. Returns <c>null</c> when the
    /// profile has no picture or has been anonymized (the DB content-type
    /// column is null), so a stale on-disk file left behind by a failed
    /// anonymization cleanup is not served. Centralizing the read path here
    /// keeps controllers free of <see cref="IFileStorage"/>.
    /// </summary>
    Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Issue nobodies-collective/Humans#702: snapshot for the profile-picture
    /// DB→FS migration verification page. Returns the total count of profiles
    /// with a custom picture (<c>ProfilePictureContentType IS NOT NULL</c>),
    /// how many of those have the expected file on disk, and the DB-only rows
    /// (the at-risk laggards that need to be migrated before Phase 2 drops
    /// <c>Profile.ProfilePictureData</c>). FS existence is checked with
    /// <see cref="IFileStorage.TryReadAsync"/> against the same key the read
    /// path uses; the FS-key helper stays section-internal.
    /// </summary>
    Task<ProfilePictureMigrationSnapshot> GetProfilePictureMigrationSnapshotAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Persists a new custom profile picture for the user's profile. No-op (logs a
    /// warning) if the user has no profile yet. Invalidates the FullProfile cache
    /// entry via the caching decorator. Callers are responsible for validating and
    /// resizing the image before calling.
    /// </summary>
    Task SetProfilePictureAsync(Guid userId, byte[] pictureData, string contentType, CancellationToken ct = default);
    Task<Guid> SaveProfileAsync(Guid userId, string displayName, ProfileSaveRequest request, string language, CancellationToken ct = default);
    Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Single canonical person-search method. Matches <paramref name="query"/>
    /// against the buckets named by <paramref name="fields"/> and returns up
    /// to <paramref name="limit"/> matches in unspecified order — callers
    /// sort + take(N) at the presentation layer per
    /// <c>memory/architecture/display-sort-in-controllers.md</c>.
    ///
    /// <para>Implicit scope: the service always filters to "not rejected,
    /// not deleted" — the only population anyone is searching. Emergency
    /// contact data is never reachable regardless of which bits are set.</para>
    ///
    /// <para>Auth boundary is the controller per design-rules §6: services
    /// are auth-free, so a non-admin endpoint passing
    /// <see cref="PersonSearchFields.Admin"/> is a programmer error caught
    /// in code review, not a runtime check.</para>
    /// </summary>
    Task<IReadOnlyList<HumanSearchResult>> SearchProfilesAsync(
        string query,
        PersonSearchFields fields,
        int limit = 10,
        CancellationToken ct = default);

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
    /// Records a reviewer's consent-check decision. <paramref name="result"/>
    /// must be <see cref="ConsentCheckStatus.Cleared"/> (also sets
    /// <c>IsApproved=true</c>) or <see cref="ConsentCheckStatus.Flagged"/>
    /// (sets <c>IsApproved=false</c>). The system-side
    /// <see cref="ConsentCheckStatus.Pending"/> transition lives on
    /// <see cref="SetConsentCheckPendingAsync"/>. Error keys:
    /// <c>NotFound</c>, <c>AlreadyRejected</c> (Cleared only).
    /// </summary>
    Task<OnboardingResult> RecordConsentCheckAsync(
        Guid userId, Guid reviewerId, ConsentCheckStatus result, string? notes,
        CancellationToken ct = default);

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
    /// Sets the human's suspension state. Dual-writes the legacy
    /// <c>IsSuspended</c> bool and the canonical
    /// <see cref="ProfileState"/> lifecycle marker (suspending →
    /// <see cref="ProfileState.Suspended"/>; unsuspending re-derives
    /// Active vs Stub from <see cref="Profile.HasRequiredIdentityFields"/>).
    /// Suspending also persists <paramref name="notes"/> as
    /// <c>AdminNotes</c>; unsuspending ignores notes. Error keys:
    /// <c>NotFound</c>.
    /// </summary>
    Task<OnboardingResult> SetSuspendedAsync(
        Guid userId, Guid adminId, bool suspended, string? notes,
        CancellationToken ct = default);

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
    /// <see cref="Users.IAccountDeletionService.AnonymizeExpiredAccountAsync"/>.
    /// </summary>
    Task<bool> AnonymizeExpiredProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="Profile.IsSuspended"/> to true and stamps
    /// <see cref="Profile.UpdatedAt"/> for users whose consent grace period has
    /// expired. Unlike <see cref="SetSuspendedAsync"/>, this variant does not
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

    /// <summary>
    /// Sets, updates, or clears the IBAN on the user's profile.
    /// Pass <c>null</c> or an empty string to clear. The caller is
    /// responsible for validating the IBAN before calling (use
    /// <c>IbanValidator.IsValid</c>). Writes <see cref="Domain.Enums.AuditAction.IbanSet"/>
    /// or <see cref="Domain.Enums.AuditAction.IbanRemove"/> via
    /// <see cref="IAuditLogService"/>. No-op (returns false) if the user has no
    /// profile. Used exclusively by the expense-report IBAN modal route.
    /// </summary>
    Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default);
}
