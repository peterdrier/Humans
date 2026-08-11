using Humans.Notifications.Services.Dtos;
using Humans.Notifications.Contracts;

namespace Humans.Notifications.Models;

internal sealed class NotificationRowViewModel
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ActionUrl { get; init; }
    public string ActionLabel { get; init; } = "View \u2192";
    public NotificationPriority Priority { get; init; }
    public NotificationSource Source { get; init; }
    public NotificationClass Class { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsRead { get; init; }

    public bool IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedByName { get; init; }
}

internal sealed class NotificationPopupViewModel
{
    public List<NotificationRowViewModel> Actionable { get; init; } = [];
    public List<NotificationRowViewModel> Informational { get; init; } = [];
    public IReadOnlyList<NotificationMeter> Meters { get; init; } = [];
    public int ActionableCount { get; init; }
}

internal sealed class NotificationInboxViewModel
{
    public List<NotificationRowViewModel> NeedsAttention { get; init; } = [];
    public List<NotificationRowViewModel> Informational { get; init; } = [];
    public List<NotificationRowViewModel> Resolved { get; init; } = [];
    public IReadOnlyList<NotificationMeter> Meters { get; init; } = [];
    public int UnreadCount { get; init; }
    public string? SearchTerm { get; init; }
    public string ActiveFilter { get; init; } = "all";
    public string ActiveTab { get; init; } = "unread";
}

internal sealed class NotificationBadgeViewModel
{
    public int ActionableUnreadCount { get; init; }
    public int InformationalUnreadCount { get; init; }
}
