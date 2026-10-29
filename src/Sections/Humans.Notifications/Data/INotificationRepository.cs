using Humans.Notifications.Domain;
using Humans.Notifications.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Notifications.Data;

internal interface INotificationRepository : IRepository
{
    Task AddRangeAsync(IReadOnlyList<Notification> notifications, CancellationToken ct = default);

    Task AddAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// Only when the actor is a recipient and the class matches; an already resolved row succeeds without a write.
    /// </summary>
    Task<NotificationMutationOutcome> ResolveAsync(
        Guid notificationId, Guid actorUserId, Instant now, NotificationClass expectedClass, CancellationToken ct = default);

    /// <summary>Succeeds whether or not the row was already read; NotFound when there is no recipient row.</summary>
    Task<NotificationMutationOutcome> MarkReadAsync(
        Guid notificationId, Guid userId, Instant now, CancellationToken ct = default);

    Task MarkAllReadAsync(Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Only unresolved rows of the service-selected class with the user as a recipient.
    /// </summary>
    Task BulkResolveAsync(
        IReadOnlyList<Guid> notificationIds, Guid userId, Instant now, NotificationClass expectedClass, CancellationToken ct = default);

    /// <summary>Marks the row read if needed. NotFound when there is no recipient row.</summary>
    Task<ClickThroughOutcome> ClickThroughAsync(
        Guid notificationId, Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>Resolves matching notifications shared with the user.</summary>
    Task ResolveBySourceAsync(
        Guid userId, string source, Instant now, CancellationToken ct = default, string? sourceKey = null);

    /// <summary>
    /// Across all recipients. A null
    /// <paramref name="resolvedByUserId"/> means no actor.
    /// </summary>
    Task ResolveBySourceKeyAsync(
        string source, string sourceKey, Instant now,
        Guid? resolvedByUserId, CancellationToken ct = default);

    /// <summary>For retired sources: nothing emits them and nothing can resolve them.</summary>
    Task<int> DeleteUnresolvedBySourcesAsync(
        IReadOnlyList<string> sources, CancellationToken ct = default);

    Task<int> DeleteResolvedOlderThanAsync(Instant resolvedCutoff, CancellationToken ct = default);

    Task<int> DeleteUnresolvedInformationalOlderThanAsync(
        Instant createdCutoff, CancellationToken ct = default);

    /// <summary>Unordered: callers sort. <paramref name="filterSources"/> is the source list a Shifts or Approvals filter keeps.</summary>
    Task<IReadOnlyList<NotificationRecipient>> GetInboxAsync(
        Guid userId,
        string? search,
        NotificationInboxFilter filter,
        IReadOnlyList<string> filterSources,
        NotificationInboxTab tab,
        Instant resolvedCutoff,
        CancellationToken ct = default);

    /// <summary>Unordered: callers sort.</summary>
    Task<IReadOnlyList<NotificationRecipient>> GetPopupAsync(
        Guid userId, CancellationToken ct = default);

    Task<(int Actionable, int Informational)> GetUnreadBadgeCountsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Newest first, for the GDPR export.</summary>
    Task<IReadOnlyList<NotificationRecipient>> GetAllForUserContributorAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: also deletes notifications left with no recipients and clears the user's resolver attribution.
    /// </summary>
    Task<int> EraseForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// A source row on a notification the target already has is dropped (target wins). Resolver attribution
    /// moves too.
    /// </summary>
    Task ReassignRecipientsToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        CancellationToken ct = default);
}

/// <summary>Forbidden covers both "not a recipient" and the wrong class for the operation.</summary>
internal sealed record NotificationMutationOutcome(
    bool Success,
    bool NotFound,
    bool Forbidden);

/// <summary><c>ActionUrl</c> is null when there is no recipient row or no action URL.</summary>
internal sealed record ClickThroughOutcome(
    string? ActionUrl,
    bool NotFound);

internal enum NotificationInboxFilter
{
    All,
    Action,
    Shifts,
    Approvals,
    Resolved,
}

internal enum NotificationInboxTab
{
    All,
    Unread,
}
