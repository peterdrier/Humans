using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Junction entity linking a Notification to a recipient User.
/// Tracks personal read state (ReadAt). Resolution is on the Notification, not here.
/// </summary>
public class NotificationRecipient
{
    /// <summary>
    /// FK to the notification. Part of composite PK.
    /// </summary>
    public Guid NotificationId { get; init; }

    /// <summary>
    /// FK to the recipient user. Part of composite PK.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// When the recipient read this notification. Null = unread.
    /// </summary>
    public Instant? ReadAt { get; set; }

    /// <summary>
    /// Navigation to the notification.
    /// </summary>
    public Notification Notification { get; init; } = null!;

    /// <summary>
    /// Navigation to the recipient user.
    /// </summary>
    public User User { get; init; } = null!;
}
