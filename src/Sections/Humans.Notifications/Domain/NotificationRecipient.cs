using NodaTime;

namespace Humans.Notifications.Domain;

/// <summary>
/// Junction entity linking a Notification to a recipient User.
/// Tracks personal read state (ReadAt). Resolution is on the Notification, not here.
/// </summary>
internal sealed class NotificationRecipient
{
    public Guid NotificationId { get; init; }

    /// <summary>
    /// Bare cross-section User id — no FK constraint, no navigation property.
    /// Init-only because it is half of the composite PK, which is why the
    /// account-merge fold re-FKs by remove-then-add rather than by update.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>Null = unread.</summary>
    public Instant? ReadAt { get; set; }

    public Notification Notification { get; init; } = null!;
}
