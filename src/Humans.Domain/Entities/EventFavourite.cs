using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Links a user to a favourited <see cref="Event"/>.
/// Deleted on unfavourite. Used to build the account-backed personal schedule.
/// </summary>
public class EventFavourite
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
    /// Day offset (from gate opening) of the favourited occurrence of a
    /// recurring event. Null favourites the whole event — every occurrence —
    /// which is also what event-level toggles (events card, API without a day)
    /// and rows created before this column existed mean.
    /// </summary>
    public int? DayOffset { get; set; }

    /// <summary>
    /// When the favourite was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    // Navigation properties

    /// <summary>
    /// Navigation property to the event.
    /// </summary>
    public Event Event { get; set; } = null!;
}
