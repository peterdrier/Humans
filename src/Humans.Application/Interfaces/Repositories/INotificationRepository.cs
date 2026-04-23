using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Notifications section's tables: <c>notifications</c>
/// and <c>notification_recipients</c>. The only non-test file that touches
/// those DbSets after the Notifications §15 migration lands.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// so the repository can be registered as Singleton while
/// <c>HumansDbContext</c> remains Scoped.
/// </para>
/// <para>
/// Read methods never <c>.Include</c> cross-domain navigation properties.
/// Callers resolve recipient and resolver display names via
/// <c>IUserService.GetByIdsAsync</c> and stitch them in memory.
/// </para>
/// </remarks>
public interface INotificationRepository
{
    // ==========================================================================
    // Writes — notifications
    // ==========================================================================

    /// <summary>
    /// Persists a batch of individually-targeted notifications (one per user,
    /// each with a single recipient row) in a single SaveChanges.
    /// </summary>
    Task AddRangeAsync(IReadOnlyList<Notification> notifications, CancellationToken ct = default);

    /// <summary>
    /// Persists a single shared notification (multiple recipients) in a
    /// single SaveChanges.
    /// </summary>
    Task AddAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// Marks the notification resolved (sets <c>ResolvedAt</c> and
    /// <c>ResolvedByUserId</c>) if it exists, the actor is a recipient, and
    /// it is not already resolved. Returns the outcome along with the list
    /// of recipient user ids for cache invalidation.
    /// </summary>
    Task<NotificationMutationOutcome> ResolveAsync(
        Guid notificationId, Guid actorUserId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="ResolveAsync"/> but only succeeds when the
    /// notification's <c>Class</c> is <see cref="NotificationClass.Informational"/>
    /// (actionable notifications cannot be dismissed).
    /// </summary>
    Task<NotificationMutationOutcome> DismissAsync(
        Guid notificationId, Guid actorUserId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Marks the recipient row read if the recipient exists and is not
    /// already read. Returns (true, NotFound=false) when the recipient
    /// exists (whether or not it was already read) and (false, NotFound=true)
    /// when no such recipient exists.
    /// </summary>
    Task<NotificationMutationOutcome> MarkReadAsync(
        Guid notificationId, Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Marks every unread recipient row for the user as read. Returns the
    /// number of rows updated.
    /// </summary>
    Task<int> MarkAllReadAsync(Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Bulk-resolves the subset of <paramref name="notificationIds"/> that
    /// are Actionable, unresolved, and include the user as a recipient.
    /// Returns the recipient user ids affected across all updated
    /// notifications for cache invalidation.
    /// </summary>
    Task<IReadOnlyList<Guid>> BulkResolveAsync(
        IReadOnlyList<Guid> notificationIds, Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Bulk-dismisses the subset of <paramref name="notificationIds"/> that
    /// are Informational, unresolved, and include the user as a recipient.
    /// Returns the recipient user ids affected across all updated
    /// notifications for cache invalidation.
    /// </summary>
    Task<IReadOnlyList<Guid>> BulkDismissAsync(
        IReadOnlyList<Guid> notificationIds, Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Click-through: marks the recipient row read if needed and returns
    /// the notification's <c>ActionUrl</c>. Returns (null ActionUrl, NotFound)
    /// if the recipient does not exist.
    /// </summary>
    Task<ClickThroughOutcome> ClickThroughAsync(
        Guid notificationId, Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Resolves every unresolved notification of the given source that the
    /// user is a recipient of. Used when the underlying condition is fixed
    /// (e.g., AccessSuspended → consents completed).
    /// </summary>
    Task<bool> ResolveBySourceAsync(
        Guid userId, NotificationSource source, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Deletes resolved notifications whose <c>ResolvedAt</c> is earlier
    /// than <paramref name="resolvedCutoff"/>. Returns the number of rows
    /// deleted. Used by <c>CleanupNotificationsJob</c>.
    /// </summary>
    Task<int> DeleteResolvedOlderThanAsync(Instant resolvedCutoff, CancellationToken ct = default);

    /// <summary>
    /// Deletes unresolved informational notifications whose <c>CreatedAt</c>
    /// is earlier than <paramref name="createdCutoff"/>. Returns the number
    /// of rows deleted. Used by <c>CleanupNotificationsJob</c>.
    /// </summary>
    Task<int> DeleteUnresolvedInformationalOlderThanAsync(
        Instant createdCutoff, CancellationToken ct = default);

    // ==========================================================================
    // Reads — inbox / popup
    // ==========================================================================

    /// <summary>
    /// Returns the recipient rows visible in a user's inbox under the given
    /// tab/filter/search criteria, with the full parent <c>Notification</c>
    /// entity (but no cross-domain navigations). Ordered newest first.
    /// </summary>
    Task<IReadOnlyList<NotificationRecipient>> GetInboxAsync(
        Guid userId,
        string? search,
        NotificationInboxFilter filter,
        NotificationInboxTab tab,
        Instant resolvedCutoff,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every unresolved recipient row for a user with the parent
    /// <c>Notification</c>, ordered newest first. Used by the popup.
    /// </summary>
    Task<IReadOnlyList<NotificationRecipient>> GetPopupAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns unread-badge counts for a user, split by
    /// <see cref="NotificationClass"/>.
    /// </summary>
    Task<(int Actionable, int Informational)> GetUnreadBadgeCountsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns every notification the user is a recipient of, ordered
    /// newest first. Used by the GDPR export contributor.
    /// </summary>
    Task<IReadOnlyList<NotificationRecipient>> GetAllForUserContributorAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads the recipient user ids for a set of notifications in a single
    /// query. Used to build cache-invalidation recipient sets without
    /// re-reading each notification individually.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRecipientUserIdsAsync(
        IReadOnlyCollection<Guid> notificationIds, CancellationToken ct = default);
}

/// <summary>
/// Result of a single-notification mutation. <c>Forbidden</c> covers both
/// "not a recipient" and "operation not allowed for this notification class"
/// (e.g., dismissing an actionable notification).
/// </summary>
public sealed record NotificationMutationOutcome(
    bool Success,
    bool NotFound,
    bool Forbidden,
    IReadOnlyList<Guid> AffectedUserIds);

/// <summary>
/// Result of a click-through. <c>ActionUrl</c> is null when the recipient
/// does not exist or the notification has no action URL.
/// </summary>
public sealed record ClickThroughOutcome(
    string? ActionUrl,
    bool NotFound,
    bool MarkedRead);

/// <summary>Inbox filter pill options.</summary>
public enum NotificationInboxFilter
{
    All,
    Action,
    Shifts,
    Approvals,
    Resolved,
}

/// <summary>Inbox tab options.</summary>
public enum NotificationInboxTab
{
    All,
    Unread,
}
