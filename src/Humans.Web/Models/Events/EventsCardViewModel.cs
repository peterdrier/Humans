namespace Humans.Web.Models.Events;

/// <summary>
/// Compact list of approved events rendered by <c>EventsCardViewComponent</c> —
/// a camp's events on the camp detail page, or a user's submitted events on
/// their profile. Auth-gated; approved events only.
/// </summary>
public class EventsCardViewModel
{
    public List<EventsCardRow> Rows { get; set; } = [];
}

public class EventsCardRow
{
    public Guid EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Start time already converted to the guide's local zone.</summary>
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }

    public string? VenueName { get; set; }
    public string? LocationNote { get; set; }
    public string? Host { get; set; }
    public bool IsRecurring { get; set; }
    public bool IsFavourited { get; set; }
}
