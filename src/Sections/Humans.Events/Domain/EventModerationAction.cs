using NodaTime;

namespace Humans.Events.Domain;

/// <summary>Append-only: never updated or deleted.</summary>
internal sealed class EventModerationAction
{
    public Guid Id { get; init; }
    public Guid GuideEventId { get; set; }
    public Guid ActorUserId { get; set; }
    public EventModerationActionType Action { get; set; }

    /// <summary>Required for non-approval actions.</summary>
    public string? Reason { get; set; }
    public Instant CreatedAt { get; init; }

    public Event Event { get; set; } = null!;

}
