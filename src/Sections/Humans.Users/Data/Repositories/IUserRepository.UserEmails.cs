using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Data.Repositories;

internal partial interface IUserRepository
{
    /// <summary>Read-only, unordered.</summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Detached: mutated entities must go back through a write method.</summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdForMutationAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Tracked for modification.</summary>
    Task<UserEmail?> GetUserEmailByIdAndUserIdAsync(
        Guid emailId, Guid userId, CancellationToken ct = default);

    /// <summary>Read-only.</summary>
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
    /// Account-merge fold: re-FKs source rows to target. A shared address collapses onto the
    /// target's row (IsVerified OR-combined); moved rows lose IsPrimary and IsGoogle so the
    /// target's selections stand.
    /// </summary>
    Task ReassignUserEmailsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);

    Task<IReadOnlyList<UserEmail>> GetAllUserEmailsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetUserIdsHavingAnyUserEmailAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task RemoveAllUserEmailsForUserAndSaveAsync(Guid userId, CancellationToken ct = default);

    Task RemoveAllUserEmailsForUsersAndSaveAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<bool> MarkUserEmailVerifiedAsync(
        Guid emailId, Instant now, CancellationToken ct = default);

    Task<bool> RemoveUserEmailByIdAsync(Guid emailId, CancellationToken ct = default);

    /// <summary>Rows matching any of <paramref name="emails"/>, case-insensitive, read-only.</summary>
    Task<IReadOnlyList<UserEmail>> GetUserEmailsByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct = default);

    /// <summary>userId → verified notification-target email; with several, one is picked arbitrarily.</summary>
    Task<Dictionary<Guid, string>> GetAllNotificationTargetUserEmailsAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetUserIdsByUserEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken ct = default);

    /// <summary>
    /// Applies an OAuth-reconcile plan in one transaction, so a displaced user's verified row is
    /// never deleted while the signing user's change is left undone. Sole caller:
    /// <c>UserEmailService.ReconcileOAuthIdentityAsync</c>.
    /// </summary>
    Task ApplyUserEmailReconcilePlanAsync(
        UserEmail? displacedRowToDelete,
        UserEmail? rowToDelete,
        UserEmail? rowToUpdate,
        UserEmail? rowToInsert,
        CancellationToken ct = default);

    Task AddUserEmailAsync(UserEmail email, CancellationToken ct = default);
    Task RemoveUserEmailAsync(UserEmail email, CancellationToken ct = default);

    Task UpdateUserEmailAsync(UserEmail email, CancellationToken ct = default);

    Task UpdateUserEmailsAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default);
}
