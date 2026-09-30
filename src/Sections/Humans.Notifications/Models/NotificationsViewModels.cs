using Humans.Notifications.Services.Dtos;
using Humans.Notifications.Services;

namespace Humans.Notifications.Models;

internal sealed class NotificationPopupViewModel
{
    public List<NotificationRowDto> Actionable { get; init; } = [];
    public List<NotificationRowDto> Informational { get; init; } = [];
    public IReadOnlyList<NotificationMeter> Meters { get; init; } = [];
}

internal sealed class NotificationInboxViewModel
{
    public List<NotificationRowDto> NeedsAttention { get; init; } = [];
    public List<NotificationRowDto> Informational { get; init; } = [];
    public List<NotificationRowDto> Resolved { get; init; } = [];
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
