using Humans.Users.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// Repository for <c>users</c>. Read-only queries use AsNoTracking.
/// </summary>
internal interface IUserRepository : IRepository
{
    /// <summary>Read-only; <see cref="User.UserEmails"/> is not populated.</summary>
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    Task<bool> SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default);

    Task<bool> SetLastSignInAsync(Guid userId, Instant at, CancellationToken ct = default);

    /// <summary>True only when this call set the mark; false when already marked or the user doesn't exist.</summary>
    Task<bool> SetMarkedFakeAsync(Guid userId, Instant at, CancellationToken ct = default);

    Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default);

    /// <summary>
    /// Unsuspending re-classifies <c>User.State</c> from the remaining fields. The reason belongs
    /// to the audit log and the notification, not the row.
    /// </summary>
    /// <returns>true if a row was updated; false if the user does not exist.</returns>
    Task<bool> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension, CancellationToken ct = default);

    /// <summary>
    /// Records a signup rejection on <c>User.State</c> (the classifier keeps a higher-precedence
    /// merge/deletion state). Writes <c>users.State</c> only.
    /// </summary>
    /// <returns>true if a row was updated; false if the user does not exist.</returns>
    Task<bool> SetRejectedAsync(Guid userId, CancellationToken ct = default);

    Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Tombstones the source as merged into the target: anonymizes the identity fields and locks
    /// the account out permanently.
    /// </summary>
    Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetUserIdsWithExternalLoginsAsync(CancellationToken ct = default);

    /// <summary>External logins per user; users without any are absent.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Moves every external login to the target. The login's key is (provider, key) alone, so two
    /// users never share a row and nothing needs de-duplicating.
    /// </summary>
    Task ReassignLoginsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    /// <summary>Sets <see cref="User.ContactSource"/> only when it is null; true when it was set.</summary>
    Task<bool> SetContactSourceIfNullAsync(
        Guid userId, ContactSource source, CancellationToken ct = default);

    /// <summary>
    /// Users whose <c>DeletionScheduledFor</c> has passed and whose <c>DeletionEligibleAfter</c>
    /// is null or has passed.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default);

    /// <summary>
    /// The identity half of the GDPR expiry anonymization, in one save: sentinel name and email,
    /// external logins removed, PII and deletion fields cleared, permanent lockout. Returns the
    /// prior email, name and language, or null when the user does not exist.
    /// </summary>
    Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Tracked; does not load aggregate-local collections.</summary>
    Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Read-only, with <see cref="Profile.VolunteerHistory"/>.</summary>
    Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every profile with <c>VolunteerHistory</c> and <c>Languages</c>, read-only.</summary>
    Task<IReadOnlyList<Profile>> GetAllProfilesAsync(CancellationToken ct = default);


    /// <summary>
    /// The content type (the GDPR gate checked before serving the file) and versioned storage key
    /// (null at the legacy key). Null when there is no picture, so an anonymized profile is never
    /// served a stale file.
    /// </summary>
    Task<(string ContentType, string? StorageKey)?> GetProfilePictureLocationAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>Ordered by proficiency descending, then language code.</summary>
    Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Replaces every language row of the user with <paramref name="languages"/>.</summary>
    Task ReplaceLanguagesAsync(Guid userId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default);

    /// <summary>
    /// Also updates the owning user's stored <see cref="UserState"/>, and its names when
    /// <paramref name="names"/> is given.
    /// </summary>
    Task AddAsync(Profile profile, CancellationToken ct = default, UserNames? names = null);

    /// <summary>
    /// Saves a profile from <see cref="GetByUserIdAsync"/> and the owning user's stored
    /// <see cref="UserState"/>, and its names when <paramref name="names"/> is given. A non-null CV
    /// payload is reconciled by <see cref="CVEntry.Id"/>: known ids update in place, empty or unknown
    /// ids insert, missing ids delete.
    /// </summary>
    Task UpdateAsync(
        Profile profile, CancellationToken ct = default, IReadOnlyList<CVEntry>? volunteerHistory = null,
        UserNames? names = null);

    /// <summary>
    /// Clears identifying profile fields, labels the profile "Deleted User", and
    /// removes its contact fields and volunteer history for GDPR erasure.
    /// </summary>
    /// <remarks>
    /// Returns false when no profile exists. Health data and languages are erased
    /// separately by <see cref="EraseProfileExtrasForUserAsync"/>.
    /// </remarks>
    Task<bool> AnonymizeForDeletionByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17, deletion path only: clears the Article 9 special-category
    /// field the account-merge fold deliberately leaves on archived profiles
    /// (medical conditions) and deletes the profile's language rows.
    /// </summary>
    Task<int> EraseProfileExtrasForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Suspends each listed user who has a profile and is not already suspended; returns the ids
    /// actually changed. The caller audits.
    /// </summary>
    Task<IReadOnlySet<Guid>> SuspendManyAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: moves the source profile's volunteer history and languages to the
    /// target and anonymizes the source profile's identifying fields in the same save.
    /// </summary>
    /// <remarks>
    /// History dedups on (year, event name); languages on code, keeping the higher proficiency
    /// (target wins ties). Contact fields are not touched. No-op if either user has no profile.
    /// <paramref name="keptProfile"/> and <paramref name="keptNames"/> are what the caller decided the
    /// target profile and its users row hold after the merge; they are written in the same save.
    /// </remarks>
    Task ReassignSubAggregatesToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt, Profile? keptProfile, UserNames? keptNames,
        CancellationToken ct = default);

    /// <summary>Detached and unordered: mutated entities must go back through a write method.</summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Tracked for modification.</summary>
    Task<UserEmail?> GetUserEmailForUserAsync(
        Guid emailId, Guid userId, CancellationToken ct = default);

    /// <summary>Read-only.</summary>
    Task<UserEmail?> GetUserEmailByIdAsync(Guid emailId, CancellationToken ct = default);

    /// <summary>
    /// The one address→rows read: every <see cref="UserEmail"/> row whose <c>Email</c>
    /// equals <paramref name="normalizedEmail"/> or <paramref name="alternateEmail"/>
    /// (the gmail/googlemail twin, when there is one), case-insensitive, read-only.
    /// Callers apply the ownership, verification and row-id predicates they need.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByAddressAsync(
        string normalizedEmail, string? alternateEmail, CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: re-FKs source rows to target. A shared address collapses onto the
    /// target's row (IsVerified OR-combined); moved rows lose IsPrimary so the
    /// target's selections stand.
    /// </summary>
    Task ReassignUserEmailsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);

    Task<IReadOnlyList<UserEmail>> GetAllUserEmailsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetUserIdsWithEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task RemoveUserEmailsAsync(Guid userId, CancellationToken ct = default);

    Task<bool> MarkUserEmailVerifiedAsync(
        Guid emailId, Instant now, CancellationToken ct = default);

    Task<bool> RemoveUserEmailByIdAsync(Guid emailId, CancellationToken ct = default);

    /// <summary>Rows matching any of <paramref name="emails"/>, case-insensitive, read-only.</summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct = default);

    /// <summary>
    /// Applies an OAuth-reconcile plan in one transaction, so a tag-move never deletes the old row
    /// while the new one is left unwritten. Sole caller:
    /// <c>UserEmailService.ReconcileOAuthIdentityAsync</c>.
    /// </summary>
    Task ApplyUserEmailReconcilePlanAsync(
        UserEmail? rowToDelete,
        UserEmail? rowToUpdate,
        UserEmail? rowToInsert,
        CancellationToken ct = default);

    Task AddUserEmailAsync(UserEmail email, CancellationToken ct = default);
    Task RemoveUserEmailAsync(UserEmail email, CancellationToken ct = default);

    Task UpdateUserEmailAsync(UserEmail email, CancellationToken ct = default);

    Task UpdateUserEmailsAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default);

    /// <summary>Every row, read-only, unordered.</summary>
    Task<IReadOnlyList<ContactField>> GetAllContactFieldsAsync(CancellationToken ct = default);

    /// <summary>Detached and unordered: mutated entities must go back to <see cref="BatchSaveAsync"/> as <c>toUpdate</c>.</summary>
    Task<IReadOnlyList<ContactField>> GetContactFieldsByUserIdAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Adds, updates and removes in one save.</summary>
    Task BatchSaveAsync(
        IReadOnlyList<ContactField> toAdd,
        IReadOnlyList<ContactField> toUpdate,
        IReadOnlyList<ContactField> toRemove,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: moves the source user's contact fields to the target. A source row
    /// matching a target row on <c>(FieldType, Value)</c> (case-insensitive) is dropped. No-op if
    /// the target has no profile.
    /// </summary>
    Task ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);
}

/// <summary>The names a profile save writes to the users row.</summary>
internal sealed record UserNames(string BurnerName, string FirstName, string LastName);
