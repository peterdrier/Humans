using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Append-only audit log of a moderation decision on a <see cref="GuideEvent"/>.
/// No UPDATE or DELETE — query the latest action for current status.
/// </summary>
public class ModerationAction
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the event being moderated.
    /// </summary>
    public Guid GuideEventId { get; set; }

    /// <summary>
    /// FK to the moderator who took the action.
    /// </summary>
    public Guid ActorUserId { get; set; }

    /// <summary>
    /// Type of moderation action.
    /// </summary>
    public ModerationActionType Action { get; set; }

    /// <summary>
    /// Reason for the action (required for non-approval actions).
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// When the action was taken.
    /// </summary>
    public Instant CreatedAt { get; init; }

    // Navigation properties

    /// <summary>
    /// Navigation property to the event.
    /// </summary>
    public GuideEvent GuideEvent { get; set; } = null!;

    /// <summary>
    /// Navigation property to the moderator.
    /// </summary>
    public User ActorUser { get; set; } = null!;
}
