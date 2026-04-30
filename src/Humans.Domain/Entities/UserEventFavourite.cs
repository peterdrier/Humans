using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Links a user to a favourited <see cref="GuideEvent"/>.
/// Deleted on unfavourite. Used to build the account-backed personal schedule.
/// </summary>
public class UserEventFavourite
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the user.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// FK to the favourited event.
    /// </summary>
    public Guid GuideEventId { get; set; }

    /// <summary>
    /// When the favourite was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    // Navigation properties

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// Navigation property to the event.
    /// </summary>
    public GuideEvent GuideEvent { get; set; } = null!;
}
