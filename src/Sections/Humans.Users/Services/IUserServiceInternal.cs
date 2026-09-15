using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Services;

/// <summary>
/// The section-only half of the write funnel: the row writes and lookups that only this
/// section's own services, jobs and controllers issue. <see cref="IUserService"/> keeps what
/// other sections call. Both resolve to the same <c>CachingUserService</c> instance.
/// </summary>
internal interface IUserServiceInternal : IUserService
{
    /// <summary>
    /// Declare that the user is not attending this year's event. Upserts a
    /// UserDeclared NotAttending row unless the user is already Attended (in
    /// which case the declaration is logged and ignored).
    /// </summary>
    Task DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>
    /// Undo a "not attending" declaration. Removes the record.
    /// Only works if the current status is NotAttending with Source=UserDeclared.
    /// </summary>
    Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>
    /// Bulk import historical participation data (admin backfill).
    /// </summary>
    Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, ParticipationStatus Status)> entries, CancellationToken ct = default);

    /// <summary>
    /// Applies the identity-level fields of the GDPR expiry anonymization
    /// on the User aggregate — removes <c>UserEmail</c> rows through
    /// <c>IUserRepository</c>, renames to <c>Deleted User</c>, clears
    /// phone/picture/iCal/deletion fields, sets the security stamp, and
    /// permanently locks out the account. Returns the pre-write identity
    /// slice or <c>null</c> if the user does not exist. Invalidates the
    /// UserInfo cache on success. Cross-section cascade (team
    /// memberships, role assignments, profile anonymization, shift cleanup)
    /// is owned by <see cref="IAccountDeletionService.AnonymizeExpiredAccountAsync"/>.
    /// </summary>
    Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Sets the deletion-pending fields on a user (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>, optional <c>DeletionEligibleAfter</c>).
    /// <paramref name="eligibleAfter"/> is the post-event hold date when the
    /// user is on a current event ticket, otherwise null. Returns false if
    /// the user does not exist.
    /// </summary>
    Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default);

    /// <summary>
    /// Clears deletion-pending fields (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>, <c>DeletionEligibleAfter</c>).
    /// Returns false if the user does not exist.
    /// </summary>
    Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Idempotently materializes a stub profile for a live user. Returns true
    /// only when a profile row was created. When provided, seeds the stub with
    /// the member's burner and legal names (the magic-link signup path); import/OAuth
    /// callers omit them, and the stub instead inherits BurnerName from the User row
    /// (already seeded at account creation) so it survives the #1097 Profile->User mirror.
    /// </summary>
    Task<bool> EnsureStubProfileAsync(
        Guid userId,
        string? burnerName = null,
        string? firstName = null,
        string? lastName = null,
        CancellationToken ct = default);

    /// <summary>
    /// Saves the profile fields projected into UserInfo and updates the user's
    /// display label in the same storage operation. Filesystem writes remain
    /// outside this service; picture metadata changes are returned to the
    /// orchestrator as old/current content types.
    /// </summary>
    Task<UserProfileSaveResult> SaveProfileAsync(
        Guid userId,
        UserProfileSaveCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the six dietary + medical Profile columns (the DietaryMedical page).
    /// Leaves all other profile fields untouched. MedicalConditions is GDPR Art. 9 —
    /// the caller owns ownership/authorization checks.
    /// </summary>
    Task SaveDietaryMedicalAsync(
        Guid userId,
        UserProfileDietaryMedicalCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Sets the profile-picture content-type column that gates UserInfo custom
    /// picture rendering. The caller owns filesystem writes and uses the old
    /// content type returned here to remove stale files.
    /// </summary>
    Task<UserProfilePictureContentTypeResult> SetProfilePictureContentTypeAsync(
        Guid userId,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Anonymizes profile-owned personal data for GDPR deletion and returns the
    /// previous picture metadata so the orchestrator can remove filesystem
    /// bytes after the DB read gate has been cleared.
    /// </summary>
    Task<UserProfileAnonymizeResult> AnonymizeProfileForDeletionAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Reconciles the profile's volunteer-history rows. Returns false when no
    /// profile exists for the user.
    /// </summary>
    Task<bool> SaveProfileVolunteerHistoryAsync(
        Guid userId,
        IReadOnlyList<CVEntry> entries,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces a profile's language rows and returns the owning user id so
    /// cache decorators can refresh the affected UserInfo entry.
    /// </summary>
    Task<UserProfileLanguagesSaveResult> SaveProfileLanguagesAsync(
        Guid profileId,
        IReadOnlyList<ProfileLanguageInfo> languages,
        CancellationToken ct = default);

    /// <summary>
    /// Suspends the given users for missing consent and returns the user ids
    /// that were actually mutated.
    /// </summary>
    Task<IReadOnlySet<Guid>> SuspendProfilesForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Adds a Users-owned email row and applies the primary / Google row
    /// invariants. Does not generate verification tokens, send email, create
    /// account-merge requests, or touch external-login rows.
    /// </summary>
    Task<UserEmailAddResult> AddUserEmailAsync(
        Guid userId,
        UserEmailAddCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Updates mutable UserEmail row state through one invariant-aware command
    /// instead of one public method per flag transition.
    /// </summary>
    Task<bool> UpdateUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailUpdateCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a Users-owned email row and optionally repairs primary / Google
    /// invariants. External login removal is orchestrated by callers before
    /// invoking this storage command.
    /// </summary>
    Task<bool> RemoveUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailRemoveCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Applies an OAuth reconcile row plan and repairs UserEmail invariants for
    /// every affected user. OAuth policy, external login state, and audit rows
    /// remain outside this storage command.
    /// </summary>
    Task<UserEmailReconcilePlanResult> ApplyUserEmailReconcilePlanAsync(
        Guid userId,
        UserEmailReconcilePlanCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Tombstones source user as merged into target. Sets
    /// <c>MergedToUserId</c>, <c>MergedAt</c>, locks the source out
    /// (<c>LockoutEnd</c> far future), and applies the existing per-user
    /// anonymization fields (display name, picture, phone, security stamp,
    /// iCal token). Returns true if the source row existed; false if it
    /// was missing. Invalidates the UserInfo cache for the source on
    /// success. Used by <c>AccountMergeService.AcceptAsync</c> as the
    /// final step of the fold-into-target flow.
    /// </summary>
    Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns userIds of users that have AspNetUserLogins rows but zero
    /// UserEmail rows. Used by EmailProblems admin scan.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns every <c>AspNetUserLogins</c> <c>(LoginProvider, ProviderKey)</c>
    /// row for each of the given users, grouped by <c>UserId</c>. Users without
    /// any external login are absent from the dictionary. Used by the per-user
    /// admin emails diagnostic and the OAuth-reconcile mother-of-all
    /// cross-user-collision log.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}
