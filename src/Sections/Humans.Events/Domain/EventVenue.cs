namespace Humans.Events.Domain;

/// <summary>
/// Admin-managed communal or infrastructure space for individual event submissions
/// (e.g. "Main Stage", "The Middle of Elsewhere").
/// </summary>
internal sealed class EventVenue
{
    public Guid Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Grid address or text description of location.
    /// </summary>
    public string? LocationDescription { get; set; }

    /// <summary>
    /// Whether this venue is active and available for event submissions.
    /// </summary>
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }

    // Navigation properties
    public ICollection<Event> Events { get; } = new List<Event>();
}
