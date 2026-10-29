using Humans.Base.Interfaces;
using Humans.Notifications.Contracts;

namespace Humans.Notifications.Services;

internal interface INotificationInboxService
    : IApplicationService, INotificationAutoResolve, INotificationRetention
{
    Task<NotificationInboxResult> GetInboxAsync(
        Guid userId, string? search, string filter, string tab,
        CancellationToken ct = default);

    Task<NotificationPopupResult> GetPopupAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>NotFound when missing; Forbidden when the user isn't a recipient or it isn't actionable.</summary>
    Task<NotificationActionResult> ResolveAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default);

    /// <summary>NotFound when missing; Forbidden when the user isn't a recipient or it is actionable.</summary>
    Task<NotificationActionResult> DismissAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default);

    Task<NotificationActionResult> MarkReadAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default);

    Task MarkAllReadAsync(
        Guid userId,
        CancellationToken ct = default);

    Task BulkResolveAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default);

    Task BulkDismissAsync(
        List<Guid> notificationIds, Guid userId,
        CancellationToken ct = default);

    /// <summary>Marks it read. Null when not found, not a recipient, or there is no action URL.</summary>
    Task<string?> ClickThroughAsync(
        Guid notificationId, Guid userId,
        CancellationToken ct = default);

    Task<(int Actionable, int Informational)> GetUnreadBadgeCountsAsync(
        Guid userId, CancellationToken ct = default);
}

internal sealed record NotificationRowDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ActionUrl { get; init; }
    public NotificationPriority Priority { get; init; }
    public string Source { get; init; } = string.Empty;
    public NotificationClass Class { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsRead { get; init; }
    public bool IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedByName { get; init; }
}

internal sealed record NotificationInboxResult
{
    public List<NotificationRowDto> NeedsAttention { get; init; } = [];
    public List<NotificationRowDto> Informational { get; init; } = [];
    public List<NotificationRowDto> Resolved { get; init; } = [];
    public int UnreadCount { get; init; }

    /// <summary>
    /// The tab actually queried: the Resolved filter forces "all". The view highlights this one, so the pill
    /// always matches the rows.
    /// </summary>
    public string EffectiveTab { get; init; } = "unread";
}

internal sealed record NotificationPopupResult
{
    public List<NotificationRowDto> Actionable { get; init; } = [];
    public List<NotificationRowDto> Informational { get; init; } = [];
}

internal sealed record NotificationActionResult(
    bool Success,
    bool NotFound = false,
    bool Forbidden = false);
