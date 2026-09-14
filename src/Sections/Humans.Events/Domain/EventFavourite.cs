using NodaTime;

namespace Humans.Events.Domain;

/// <summary>
/// Links a user to a favourited <see cref="Event"/>.
/// Deleted on unfavourite. Used to build the account-backed personal schedule.
/// </summary>
internal sealed class EventFavourite
{
    public Guid Id { get; init; }
    public Guid UserId { get; set; }
    public Guid GuideEventId { get; set; }

    /// <summary>
    /// Day offset (from gate opening) of the favourited occurrence of a
    /// recurring event. Null favourites the whole event — every occurrence —
    /// which is also what event-level toggles (events card, API without a day)
    /// and rows created before this column existed mean.
    /// </summary>
    public int? DayOffset { get; set; }
    public Instant CreatedAt { get; init; }

    // Navigation properties
    public Event Event { get; set; } = null!;
}
