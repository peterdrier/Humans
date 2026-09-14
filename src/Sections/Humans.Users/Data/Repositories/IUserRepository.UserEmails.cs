using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// User-owned storage operations for the <c>user_emails</c> table.
/// </summary>
internal partial interface IUserRepository
{
    /// <summary>
    /// Returns all emails for a user, read-only, unordered — callers sort for
    /// display (alphabetically on <c>Email</c>).
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns detached entities intended to be mutated in-memory and passed back
    /// to <see cref="UpdateUserEmailAsync"/> or <see cref="UpdateUserEmailsAsync"/>. The returned
    /// entities are NOT tracked — callers must explicitly hand mutated entities
    /// back to a write method for persistence.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdForMutationAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single email by id and user id, tracked for modification.
    /// </summary>
    Task<UserEmail?> GetUserEmailByIdAndUserIdAsync(
        Guid emailId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single email by id, read-only.
    /// </summary>
    Task<UserEmail?> GetUserEmailByIdReadOnlyAsync(Guid emailId, CancellationToken ct = default);

    /// <summary>
    /// The one address→rows read: every <see cref="UserEmail"/> row whose <c>Email</c>
    /// equals <paramref name="normalizedEmail"/> or <paramref name="alternateEmail"/>
    /// (the gmail/googlemail twin, when there is one), case-insensitive, read-only.
    /// Callers apply the ownership, verification and row-id predicates they need.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByAddressAsync(
        string normalizedEmail, string? alternateEmail, CancellationToken ct = default);

    /// <summary>
    /// Bulk-moves <c>user_emails</c> rows from <paramref name="sourceUserId"/>
    /// to <paramref name="targetUserId"/> for the account-merge fold flow.
    /// Conflict rule per the fold spec: when source and target both have a
    /// row for the same address (case-insensitive), the rows collapse —
    /// <c>IsVerified</c> is OR-combined onto the target's row and the
    /// source's row is deleted. Surviving source rows are re-FK'd to target
    /// with <c>IsPrimary</c> and <c>IsGoogle</c> cleared so the target's
    /// existing primary / Google selections remain authoritative.
    /// <c>UpdatedAt</c> is stamped to <paramref name="updatedAt"/> on every
    /// row touched. Returns the count of <c>user_emails</c> rows ultimately
    /// attributed to <paramref name="targetUserId"/>.
    /// </summary>
    Task<int> ReassignUserEmailsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every user email, read-only. Used by the duplicate-account
    /// scan to detect overlapping addresses across users. Trivial to load in
    /// full at our small scale.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetAllUserEmailsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of <paramref name="userIds"/> that have at least one
    /// <see cref="UserEmail"/> row.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsHavingAnyUserEmailAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Removes every <see cref="UserEmail"/> row for the given user. Used
    /// during account merge/duplicate-resolve to wipe the source's addresses
    /// before anonymization.
    /// </summary>
    Task RemoveAllUserEmailsForUserAndSaveAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Removes every <see cref="UserEmail"/> row for the given users.
    /// </summary>
    Task RemoveAllUserEmailsForUsersAndSaveAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Marks a single email as verified and bumps <see cref="UserEmail.UpdatedAt"/>
    /// to <paramref name="now"/>. Returns false if the email does not exist.
    /// Used by <c>AccountMergeService.AcceptAsync</c> to complete a merge.
    /// </summary>
    Task<bool> MarkUserEmailVerifiedAsync(
        Guid emailId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Removes a single email by id. Returns false if the email does not
    /// exist. Used by <c>AccountMergeService.RejectAsync</c> to clear the
    /// pending unverified address on rejection.
    /// </summary>
    Task<bool> RemoveUserEmailByIdAsync(Guid emailId, CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="UserEmail"/> row whose <c>Email</c> matches one
    /// of <paramref name="emails"/> (case-insensitive). Read-only (AsNoTracking).
    /// Used by the Google admin workspace-accounts list to match Google-side
    /// accounts to human records without loading the full table.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct = default);

    /// <summary>
    /// Returns a mapping of userId → verified notification-target email for all users
    /// that have one. If a user has multiple verified notification-target emails,
    /// one is picked arbitrarily.
    /// </summary>
    Task<Dictionary<Guid, string>> GetAllNotificationTargetUserEmailsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns distinct user ids whose email starts with <paramref name="prefix"/>
    /// and ends with <paramref name="suffix"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsByUserEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken ct = default);

    /// <summary>
    /// Issue nobodies-collective/Humans#697. Applies a single OAuth-reconcile
    /// data change inside one <see cref="DbContext"/> + one
    /// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/>.
    /// Atomicity guarantee: when the plan includes a cross-user displaced
    /// row alongside the signing user's mutation, either every operation
    /// commits or none of them do — the displaced user's verified row can
    /// never be deleted while the signing user's mutation is left undone.
    /// Sole legitimate caller:
    /// <c>UserEmailService.ReconcileOAuthIdentityAsync</c>. All parameters
    /// are optional; a no-op call is allowed but pointless.
    /// </summary>
    Task ApplyUserEmailReconcilePlanAsync(
        UserEmail? displacedRowToDelete,
        UserEmail? rowToDelete,
        UserEmail? rowToUpdate,
        UserEmail? rowToInsert,
        CancellationToken ct = default);

    /// <summary>
    /// Single-transaction flip: sets <see cref="UserEmail.IsGoogle"/> = true
    /// on the target row, and IsGoogle = false on every sibling row for the
    /// same user. Stamps <c>UpdatedAt</c> with <paramref name="updatedAt"/>
    /// on every row whose <c>IsGoogle</c> value changes. Owner-gate
    /// (userId match) is performed by the caller.
    /// </summary>
    Task SetUserEmailGoogleExclusiveAsync(Guid userId, Guid userEmailId, Instant updatedAt, CancellationToken cancellationToken = default);

    Task AddUserEmailAsync(UserEmail email, CancellationToken ct = default);
    Task RemoveUserEmailAsync(UserEmail email, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to a single <see cref="UserEmail"/> entity by attaching it
    /// to a fresh context and marking it as Modified.
    /// </summary>
    Task UpdateUserEmailAsync(UserEmail email, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to multiple <see cref="UserEmail"/> entities in one
    /// SaveChanges call. Each entity is attached and marked as Modified.
    /// </summary>
    Task UpdateUserEmailsAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default);
}
