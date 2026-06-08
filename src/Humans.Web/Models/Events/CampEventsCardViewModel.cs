namespace Humans.Web.Models.Events;

/// <summary>
/// Compact list of a camp's approved events rendered on the camp detail page
/// by <c>CampEventsViewComponent</c>. Auth-gated; approved events only.
/// </summary>
public class CampEventsCardViewModel
{
    /// <summary>Camp slug — the favourite toggle redirects back to this camp's detail page.</summary>
    public string CampSlug { get; set; } = string.Empty;

    public List<CampEventsCardRow> Rows { get; set; } = [];
}

public class CampEventsCardRow
{
    public Guid EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>Start time already converted to the guide's local zone.</summary>
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }

    public string? VenueName { get; set; }
    public string? LocationNote { get; set; }
    public string? Host { get; set; }
    public bool IsRecurring { get; set; }
    public bool IsFavourited { get; set; }
}
