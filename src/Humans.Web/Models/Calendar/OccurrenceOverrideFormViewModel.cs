using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Models.Calendar;

public class OccurrenceOverrideFormViewModel
{
    public Guid EventId { get; set; }

    /// <summary>ISO-8601 UTC string used as the URL segment.</summary>
    public string OriginalOccurrenceStartUtc { get; set; } = string.Empty;

    public DateTime? OverrideStartLocal { get; set; }
    public DateTime? OverrideEndLocal   { get; set; }
    public string? OverrideTitle        { get; set; }
    public string? OverrideDescription  { get; set; }
    public string? OverrideLocation     { get; set; }
    public string? OverrideLocationUrl  { get; set; }

    public string RecurrenceTimezone { get; set; } = "Europe/Madrid";

    public static Instant ParseOriginal(string s) =>
        InstantPattern.ExtendedIso.Parse(s).Value;
}
