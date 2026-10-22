namespace Humans.Events.Domain;

/// <summary>Sensitive categories trigger the attendee opt-out UI.</summary>
internal sealed class EventCategory
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>URL-safe, unique.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Triggers the opt-out UI (Adult, Spiritual, etc.).</summary>
    public bool IsSensitive { get; set; }
    public int DisplayOrder { get; set; }

    /// <summary>Inactive categories are hidden from submission forms.</summary>
    public bool IsActive { get; set; } = true;

    public ICollection<Event> Events { get; } = new List<Event>();
}
