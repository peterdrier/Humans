using NodaTime;

namespace Humans.Events.Domain;

/// <summary>
/// Append-only audit log of a moderation decision on a <see cref="Event"/>.
/// No UPDATE or DELETE — query the latest action for current status.
/// </summary>
internal sealed class EventModerationAction
{
    public Guid Id { get; init; }
    public Guid GuideEventId { get; set; }
    public Guid ActorUserId { get; set; }
    public EventModerationActionType Action { get; set; }

    /// <summary>
    /// Reason for the action (required for non-approval actions).
    /// </summary>
    public string? Reason { get; set; }
    public Instant CreatedAt { get; init; }

    // Navigation properties
    public Event Event { get; set; } = null!;

}
