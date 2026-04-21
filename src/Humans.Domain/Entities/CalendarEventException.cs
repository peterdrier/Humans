using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// An override for a single occurrence of a recurring <see cref="CalendarEvent"/>.
/// Either cancels the occurrence or overrides one or more fields for it.
/// </summary>
public class CalendarEventException
{
    public Guid Id { get; init; }
    public Guid EventId { get; set; }
    public CalendarEvent Event { get; set; } = null!;
    public Instant OriginalOccurrenceStartUtc { get; set; }
    public bool IsCancelled { get; set; }
    public Instant? OverrideStartUtc { get; set; }
    public Instant? OverrideEndUtc { get; set; }
    public string? OverrideTitle { get; set; }
    public string? OverrideDescription { get; set; }
    public string? OverrideLocation { get; set; }
    public string? OverrideLocationUrl { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var hasOverride =
            OverrideStartUtc is not null ||
            OverrideEndUtc is not null ||
            OverrideTitle is not null ||
            OverrideDescription is not null ||
            OverrideLocation is not null ||
            OverrideLocationUrl is not null;

        if (!IsCancelled && !hasOverride)
            return new[] { "Exception must either cancel the occurrence or override at least one field." };

        return Array.Empty<string>();
    }
}
