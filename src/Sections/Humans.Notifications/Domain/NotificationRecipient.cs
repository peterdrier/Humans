using NodaTime;

namespace Humans.Notifications.Domain;

/// <summary>Read state is per recipient; resolution lives on the Notification.</summary>
internal sealed class NotificationRecipient
{
    public Guid NotificationId { get; init; }

    /// <summary>Init-only as half of the composite key, so the merge fold re-FKs by remove-then-add.</summary>
    public Guid UserId { get; init; }

    public Instant? ReadAt { get; set; }

    public Notification Notification { get; init; } = null!;
}
