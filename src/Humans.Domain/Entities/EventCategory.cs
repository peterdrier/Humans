namespace Humans.Domain.Entities;

/// <summary>
/// Lookup table for event guide categories (e.g. Workshop, Music, Adult).
/// Sensitive categories trigger the opt-out UI for attendees.
/// </summary>
public class EventCategory
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Display name (e.g. "Workshop", "Adult").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL-safe identifier, unique (e.g. "workshop", "adult").
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Whether this category triggers the opt-out UI (Adult, Spiritual, etc.).
    /// </summary>
    public bool IsSensitive { get; set; }

    /// <summary>
    /// Sort order for display.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Whether this category is active. Inactive categories are hidden from submission forms.
    /// </summary>
    public bool IsActive { get; set; } = true;

    // Navigation properties

    /// <summary>
    /// Navigation property to events in this category.
    /// </summary>
    public ICollection<GuideEvent> GuideEvents { get; } = new List<GuideEvent>();
}
