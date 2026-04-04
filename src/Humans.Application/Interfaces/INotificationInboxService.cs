using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for notification inbox read models and write operations:
/// inbox/popup queries, resolve/dismiss, mark-read, and badge cache invalidation.
/// Complements INotificationService (which handles dispatching new notifications).
/// </summary>
public interface INotificationInboxService
{
    /// <summary>
    /// Builds the notification inbox for a user with filtering, search, and tab support.
    /// </summary>
    Task<NotificationInboxResult> GetInboxAsync(
        Guid userId, string? search, string filter, string tab,
        CancellationToken ct = default);

    /// <summary>
    /// Builds the notification popup (unresolved notifications) for a user.
    /// </summary>
    Task<NotificationPopupResult> GetPopupAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a notification (marks it as handled by the user).
    /// Returns false if the notification was not found or user is not a recipient.
    /// </summary>
    Task<NotificationActionResult> ResolveAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Dismisses an informational notification.
    /// Returns false if the notification is actionable (cannot be dismissed).
    /// </summary>
    Task<NotificationActionResult> DismissAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a notification as read for the user.
    /// </summary>
    Task<NotificationActionResult> MarkReadAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Marks all unread notifications as read for the user.
    /// </summary>
    Task MarkAllReadAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Bulk-resolves selected actionable notifications.
    /// </summary>
    Task BulkResolveAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Bulk-dismisses selected informational notifications.
    /// </summary>
    Task BulkDismissAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Click-through: marks a notification as read and returns the action URL.
    /// Returns null if the notification is not found or user is not a recipient.
    /// </summary>
    Task<string?> ClickThroughAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default);
}

/// <summary>
/// A single notification row in inbox/popup views.
/// </summary>
public record NotificationRowDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Body { get; init; }
    public string? ActionUrl { get; init; }
    public string? ActionLabel { get; init; }
    public NotificationPriority Priority { get; init; }
    public NotificationSource Source { get; init; }
    public NotificationClass Class { get; init; }
    public string? TargetGroupName { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsRead { get; init; }
    public bool IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedByName { get; init; }
    public List<string> RecipientInitials { get; init; } = [];
    public int TotalRecipientCount { get; init; }
}

/// <summary>
/// Result of the inbox query.
/// </summary>
public record NotificationInboxResult
{
    public List<NotificationRowDto> NeedsAttention { get; init; } = [];
    public List<NotificationRowDto> Informational { get; init; } = [];
    public List<NotificationRowDto> Resolved { get; init; } = [];
    public int UnreadCount { get; init; }
}

/// <summary>
/// Result of the popup query.
/// </summary>
public record NotificationPopupResult
{
    public List<NotificationRowDto> Actionable { get; init; } = [];
    public List<NotificationRowDto> Informational { get; init; } = [];
    public int ActionableCount { get; init; }
}

/// <summary>
/// Result of a single notification action (resolve, dismiss, mark-read).
/// </summary>
public record NotificationActionResult(
    bool Success,
    bool NotFound = false,
    bool Forbidden = false);
