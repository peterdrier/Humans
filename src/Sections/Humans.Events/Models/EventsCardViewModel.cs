namespace Humans.Events.Models;

/// <summary>An approved event on a camp's detail page or a user's profile.</summary>
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
