namespace Humans.Events.Models;

/// <summary>A camp's events on its detail page, or a user's on their profile. Approved events only.</summary>
internal sealed class EventsCardViewModel
{
    public List<EventsCardRow> Rows { get; set; } = [];
}

internal sealed class EventsCardRow
{
    public Guid EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Already in the guide's local zone.</summary>
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }

    public string? VenueName { get; set; }
    public string? LocationNote { get; set; }
    public string? Host { get; set; }
    public bool IsRecurring { get; set; }
    public bool IsFavourited { get; set; }
}
