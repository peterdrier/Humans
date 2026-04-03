using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class NotificationRowViewModel
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Body { get; init; }
    public string? ActionUrl { get; init; }
    public string ActionLabel { get; init; } = "View \u2192";
    public NotificationPriority Priority { get; init; }
    public NotificationSource Source { get; init; }
    public NotificationClass Class { get; init; }
    public string? TargetGroupName { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsRead { get; init; }

    // Resolution
    public bool IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedByName { get; init; }

    // Group avatar cluster data
    public List<string> RecipientInitials { get; init; } = [];
    public int TotalRecipientCount { get; init; }
}

public class NotificationPopupViewModel
{
    public List<NotificationRowViewModel> Actionable { get; init; } = [];
    public List<NotificationRowViewModel> Informational { get; init; } = [];
    public IReadOnlyList<NotificationMeter> Meters { get; init; } = [];
    public int ActionableCount { get; init; }
}

public class NotificationInboxViewModel
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

public class NotificationBadgeViewModel
{
    public int ActionableUnreadCount { get; init; }
    public int InformationalUnreadCount { get; init; }
}
