using NodaTime;
using NodaTime.Text;

namespace Humans.Calendar.Models;

internal sealed class OccurrenceOverrideFormViewModel
{
    public Guid EventId { get; set; }

    /// <summary>ISO-8601 UTC string used as the URL segment.</summary>
    public string OriginalOccurrenceStartUtc { get; set; } = string.Empty;

    public DateTime? OverrideStartLocal { get; set; }
    public DateTime? OverrideEndLocal { get; set; }
    public string? OverrideTitle { get; set; }
    public string? OverrideDescription { get; set; }
    public string? OverrideLocation { get; set; }
    public string? OverrideLocationUrl { get; set; }

    public string RecurrenceTimezone { get; set; } = "Europe/Madrid";

    /// <summary>
    /// Parses the <c>{originalStartUtc}</c> route segment, or null when it is not a
    /// valid ISO-8601 instant. Null means the URL names no occurrence, so callers
    /// answer 404 — a hand-typed or truncated segment is a missing page, not a fault.
    /// </summary>
    public static Instant? TryParseOriginal(string s)
    {
        var result = InstantPattern.ExtendedIso.Parse(s);
        return result.Success ? result.Value : null;
    }
}
