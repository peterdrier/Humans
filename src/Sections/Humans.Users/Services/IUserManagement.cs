using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Services;

/// <summary>
/// The section's internal face: the public reads and writes, the merge seam, and the row commands
/// only Users itself issues. Every face resolves to the same <c>CachingUserService</c>.
/// </summary>
internal interface IUserManagement : IUserService, IUserWrite, IUserMerge
{
    /// <summary>
    /// Suspends or unsuspends: writes <c>users.State</c> only (unsuspending re-classifies from the
    /// remaining signals). <paramref name="adminSuspension"/> picks <see cref="UserState.AdminSuspended"/>
    /// over the consent <see cref="UserState.Suspended"/>. <c>NotFound</c> without a profile. Audit,
    /// notification and the reason belong to the caller.
    /// </summary>
    Task<OnboardingResult> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension = false, CancellationToken ct = default);

    /// <summary>
    /// Users whose <c>DeletionScheduledFor</c> has passed and whose <c>DeletionEligibleAfter</c>
    /// is null or has passed.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default);

    /// <summary>
    /// The identity half of the GDPR expiry anonymization; returns the prior identity, or null when
    /// the user does not exist. The cross-section cascade is
    /// <see cref="IAccountDeletionService.AnonymizeExpiredAccountAsync"/>'s.
    /// </summary>
    Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="eligibleAfter"/> is the post-event hold date for a current ticket holder.
    /// False if the user does not exist.
    /// </summary>
    Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default);

    Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Idempotent; true only when a row was created. Names seed the stub on magic-link signup;
    /// other callers omit them and the stub inherits BurnerName from the User row, so it survives
    /// the Profile→User name mirror. An admin actor gets a repair audit.
    /// </summary>
    Task<bool> EnsureStubProfileAsync(
        Guid userId,
        string? burnerName = null,
        string? firstName = null,
        string? lastName = null,
        CancellationToken ct = default,
        Guid? actorUserId = null);

    /// <summary>
    /// Saves the profile fields and the user's display label together. Picture bytes are written
    /// before their metadata, so a failed write keeps the previous picture. A null CV is left alone.
    /// </summary>
    Task<UserProfileSaveResult> SaveProfileAsync(
        Guid userId,
        UserProfileSaveCommand command,
        byte[]? profilePictureData = null,
        CancellationToken ct = default,
        IReadOnlyList<CVEntry>? volunteerHistory = null);

    /// <summary>
    /// Creates the profile row if needed and touches no other field. GDPR Art. 9: the caller owns
    /// the authorization check.
    /// </summary>
    Task SaveMedicalConditionsAsync(
        Guid userId,
        string? medicalConditions,
        CancellationToken ct = default);

    /// <summary>
    /// Clears identifying profile fields and returns the previous picture metadata.
    /// </summary>
    Task<UserProfileAnonymizeResult> AnonymizeProfileForDeletionAsync(
        Guid userId,
        CancellationToken ct = default);

    Task<UserProfileLanguagesSaveResult> SaveProfileLanguagesAsync(
        Guid profileId,
        IReadOnlyList<ProfileLanguageInfo> languages,
        CancellationToken ct = default);

    /// <summary>Returns the ids actually changed.</summary>
    Task<IReadOnlySet<Guid>> SuspendProfilesForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Applies the primary / Google invariants only: no verification token, email, merge request
    /// or external-login change.
    /// </summary>
    Task<UserEmailAddResult> AddUserEmailAsync(
        Guid userId,
        UserEmailAddCommand command,
        CancellationToken ct = default);

    Task<UpdateEmailResult> UpdateUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailUpdateCommand command,
        CancellationToken ct = default);

    /// <summary>Callers remove any external login first.</summary>
    Task<bool> RemoveUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailRemoveCommand command,
        CancellationToken ct = default);

    /// <summary>Storage only: OAuth policy, external logins and audit stay with the caller.</summary>
    Task<UserEmailReconcilePlanResult> ApplyUserEmailReconcilePlanAsync(
        Guid userId,
        UserEmailReconcilePlanCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Tombstones the source as merged into the target: anonymizes the identity fields and locks it
    /// out. False if the source was missing. The final step of the merge.
    /// </summary>
    Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default);

    /// <summary>External logins per user; users without any are absent.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}
