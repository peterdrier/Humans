using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// A notification dispatched to one or more recipients.
/// Resolution is shared: when any recipient resolves, it resolves for all.
/// </summary>
public class Notification
{
    public Guid Id { get; init; }

    /// <summary>
    /// Short title displayed in the notification row.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional body text with additional context.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    /// Optional URL for the action button (e.g. link to team page, review queue).
    /// </summary>
    public string? ActionUrl { get; set; }

    /// <summary>
    /// Optional button label text (e.g. "Review →", "Approve →", "Find cover →").
    /// Falls back to "View →" in UI if null.
    /// </summary>
    public string? ActionLabel { get; set; }

    /// <summary>
    /// Priority level affecting visual presentation.
    /// </summary>
    public NotificationPriority Priority { get; set; }

    /// <summary>
    /// Source system that generated this notification.
    /// Maps to MessageCategory for preference checks.
    /// </summary>
    public NotificationSource Source { get; set; }

    /// <summary>
    /// Classification: Informational (dismissable) or Actionable (requires action).
    /// </summary>
    public NotificationClass Class { get; set; }

    /// <summary>
    /// Display name for group-targeted notifications (e.g. "Coordinators", "Board").
    /// Null for individual-targeted notifications.
    /// </summary>
    public string? TargetGroupName { get; set; }

    /// <summary>
    /// When the notification was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the notification was resolved (handled/dismissed). Null = unresolved.
    /// </summary>
    public Instant? ResolvedAt { get; set; }

    /// <summary>
    /// Who resolved this notification. Null = unresolved.
    /// </summary>
    public Guid? ResolvedByUserId { get; set; }

    /// <summary>
    /// Navigation to the user who resolved this notification.
    /// </summary>
    public User? ResolvedByUser { get; set; }

    /// <summary>
    /// Recipients who can see this notification.
    /// </summary>
    public ICollection<NotificationRecipient> Recipients { get; init; } = new List<NotificationRecipient>();
}
