using NodaTime;

namespace Humans.Events.Domain;

/// <summary>Deleted on unfavourite.</summary>
internal sealed class EventFavourite
{
    public Guid Id { get; init; }
    public Guid UserId { get; set; }
    public Guid GuideEventId { get; set; }

    /// <summary>
    /// Day offset from gate opening of the favourited occurrence. Null favourites every occurrence, which is also what
    /// event-level toggles and rows older than this column mean.
    /// </summary>
    public int? DayOffset { get; set; }
    public Instant CreatedAt { get; init; }

    public Event Event { get; set; } = null!;
}
