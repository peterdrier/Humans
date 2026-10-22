using Humans.Users.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// Repository for <c>AspNetUsers</c> and <c>event_participations</c>. Reads are AsNoTracking;
/// event-participation writes are load-then-save so <c>UserService</c> applies its rules first.
/// </summary>
internal partial interface IUserRepository : IRepository
{
    /// <summary>Read-only; <see cref="User.UserEmails"/> is not populated.</summary>
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    Task<bool> SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default);

    Task<bool> SetLastLoginAsync(Guid userId, Instant at, CancellationToken ct = default);

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

    /// <summary>
    /// Deletes the users and their external logins. The caller must first clear cross-section
    /// references and UserEmail rows.
    /// </summary>
    Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

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

    // Frozen legacy event participations: Tickets owns participation and reads these through UserInfo.

    /// <summary>All years, ordered by year, read-only.</summary>
    Task<IReadOnlyList<EventParticipation>> GetEventParticipationsByUserIdAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Grouped by <c>UserId</c>; users with none are absent.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<EventParticipation>>>
        GetEventParticipationsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}
