namespace Humans.Domain.Entities;

/// <summary>
/// Admin-managed communal or infrastructure space for individual event submissions
/// (e.g. "Main Stage", "The Middle of Elsewhere").
/// </summary>
public class GuideSharedVenue
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Venue display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional venue description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Grid address or text description of location.
    /// </summary>
    public string? LocationDescription { get; set; }

    /// <summary>
    /// Whether this venue is active and available for event submissions.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Sort order for display.
    /// </summary>
    public int DisplayOrder { get; set; }

    // Navigation properties

    /// <summary>
    /// Navigation property to events at this venue.
    /// </summary>
    public ICollection<GuideEvent> GuideEvents { get; } = new List<GuideEvent>();
}
