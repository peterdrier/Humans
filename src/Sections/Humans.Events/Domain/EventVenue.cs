namespace Humans.Events.Domain;

/// <summary>An admin-managed space for individual event submissions, e.g. "Main Stage".</summary>
internal sealed class EventVenue
{
    public Guid Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Grid address or text description.</summary>
    public string? LocationDescription { get; set; }

    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }

    public ICollection<Event> Events { get; } = new List<Event>();
}
