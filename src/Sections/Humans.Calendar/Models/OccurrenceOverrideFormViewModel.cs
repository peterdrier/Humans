using NodaTime;
using NodaTime.Text;

namespace Humans.Calendar.Models;

internal sealed class OccurrenceOverrideFormViewModel
{
    public Guid EventId { get; set; }

    /// <summary>ISO-8601 UTC string used as the URL segment.</summary>
    public string OriginalOccurrenceStartUtc { get; set; } = string.Empty;

    public bool IsAllDay { get; set; }
    public DateTime? OverrideStartDateLocal { get; set; }
    public DateTime? OverrideEndDateLocal { get; set; }

    public DateTime? OverrideStartLocal { get; set; }
    public DateTime? OverrideEndLocal { get; set; }
    public string? OverrideTitle { get; set; }
    public string? OverrideDescription { get; set; }
    public string? OverrideLocation { get; set; }
    public string? OverrideLocationUrl { get; set; }

    public string RecurrenceTimezone { get; set; } = "Europe/Madrid";

    /// <summary>
    /// Builds the override DTO from the posted form. Returns <c>false</c> when an all-day
    /// field carries a time of day — the form offers dates there, so a non-zero time means
    /// the post did not come from the form and the caller re-renders rather than saving.
    /// </summary>
    public bool TryBuildOverride(DateTimeZone zone, out Humans.Calendar.Services.Dtos.OverrideOccurrenceDto dto)
    {
        dto = new(
            OverrideStartLocal is { } start ? LocalDateTime.FromDateTime(start).InZoneLeniently(zone).ToInstant() : null,
            OverrideEndLocal is { } end ? LocalDateTime.FromDateTime(end).InZoneLeniently(zone).ToInstant() : null,
            OverrideTitle, OverrideDescription, OverrideLocation, OverrideLocationUrl,
            OverrideStartDateLocal is { } date ? LocalDate.FromDateTime(date) : null,
            OverrideEndDateLocal is { } last ? LocalDate.FromDateTime(last).PlusDays(1) : null);
        return !(OverrideStartDateLocal?.TimeOfDay > TimeSpan.Zero || OverrideEndDateLocal?.TimeOfDay > TimeSpan.Zero);
    }

    public static LocalDate? TryParseOriginalDate(string s)
    {
        var result = LocalDatePattern.Iso.Parse(s);
        return result.Success ? result.Value : null;
    }

    public static Instant? TryParseOriginal(string s)
    {
        var result = InstantPattern.ExtendedIso.Parse(s);
        return result.Success ? result.Value : null;
    }
}
