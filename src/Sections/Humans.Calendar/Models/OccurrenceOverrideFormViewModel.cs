using Humans.Calendar.Services.Dtos;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.ComponentModel.DataAnnotations;
using NodaTime;
using NodaTime.Text;

namespace Humans.Calendar.Models;

internal sealed class OccurrenceOverrideFormViewModel
{
    public Guid EventId { get; set; }

    /// <summary>The URL identity: an ISO date for an all-day series, an ISO instant for a timed one.</summary>
    public string OriginalOccurrenceStartUtc { get; set; } = string.Empty;

    public bool IsAllDay { get; set; }
    public DateTime? OverrideStartDateLocal { get; set; }
    public DateTime? OverrideEndDateLocal { get; set; }

    public DateTime? OverrideStartLocal { get; set; }
    public DateTime? OverrideEndLocal { get; set; }
    [StringLength(200, ErrorMessage = "Validation_MaxLength")]
    public string? OverrideTitle { get; set; }
    [StringLength(4000, ErrorMessage = "Validation_MaxLength")]
    public string? OverrideDescription { get; set; }
    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? OverrideLocation { get; set; }
    [StringLength(2000, ErrorMessage = "Validation_MaxLength"), Url(ErrorMessage = "Validation_InvalidValue")]
    public string? OverrideLocationUrl { get; set; }

    [BindRequired]
    public string RecurrenceTimezone { get; set; } = "Europe/Madrid";

    /// <summary>
    /// False when an all-day field carries a time of day (the post didn't come from the form) or its inclusive end
    /// can't advance to an exclusive date. The caller re-renders rather than saving.
    /// </summary>
    public bool TryBuildOverride(DateTimeZone zone, out OverrideOccurrenceDto dto)
    {
        dto = null!;
        if (OverrideStartDateLocal?.TimeOfDay > TimeSpan.Zero || OverrideEndDateLocal?.TimeOfDay > TimeSpan.Zero
            || (OverrideEndDateLocal is { } inclusiveEnd && LocalDate.FromDateTime(inclusiveEnd) == LocalDate.MaxIsoValue))
            return false;
        dto = new(
            OverrideStartLocal is { } start ? LocalDateTime.FromDateTime(start).InZoneLeniently(zone).ToInstant() : null,
            OverrideEndLocal is { } end ? LocalDateTime.FromDateTime(end).InZoneLeniently(zone).ToInstant() : null,
            OverrideTitle, OverrideDescription, OverrideLocation, OverrideLocationUrl,
            OverrideStartDateLocal is { } date ? LocalDate.FromDateTime(date) : null,
            OverrideEndDateLocal is { } last ? LocalDate.FromDateTime(last).PlusDays(1) : null);
        return true;
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
