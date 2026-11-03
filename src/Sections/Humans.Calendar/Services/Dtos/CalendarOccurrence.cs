using NodaTime;
using Microsoft.Extensions.Localization;

namespace Humans.Calendar.Services.Dtos;

/// <summary>
/// Contributed items carry <c>Guid.Empty</c> for <see cref="EventId"/> and <see cref="OwningTeamId"/>, so views
/// link them through <see cref="Url"/> instead of <c>/Calendar/Event/{id}</c>.
/// </summary>
internal sealed record CalendarOccurrence(
    Guid EventId,
    Instant? OccurrenceStartUtc,
    Instant? OccurrenceEndUtc,
    bool IsAllDay,
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    string OwningTeamName,
    bool IsRecurring,
    Instant? OriginalOccurrenceStartUtc,
    LocalDate? StartDate = null,
    LocalDate? EndDateExclusive = null,
    LocalDate? OriginalOccurrenceDate = null,
    string Source = CalendarOccurrence.CalendarSource,
    string? Url = null)
{
    public const string CalendarSource = "Calendar";

    internal LocalDate StartLocalDate(DateTimeZone zone) =>
        StartDate ?? OccurrenceStartUtc!.Value.InZone(zone).Date;

    internal LocalDate EndLocalDate(DateTimeZone zone)
    {
        if (EndDateExclusive is { } endDateExclusive) return endDateExclusive.PlusDays(-1);
        if (OccurrenceEndUtc is not { } endUtc) return StartLocalDate(zone);
        if (endUtc <= OccurrenceStartUtc) return StartLocalDate(zone);

        // -1ns so a midnight-aligned end (00:00 next day) collapses back to the previous local date.
        var adjusted = endUtc.Minus(Duration.FromNanoseconds(1));
        var endDate = adjusted.InZone(zone).Date;
        var startDate = StartLocalDate(zone);
        return endDate < startDate ? startDate : endDate;
    }

    internal bool ShouldHideTimeLabel(DateTimeZone zone)
    {
        if (IsAllDay) return true;
        return EndLocalDate(zone) > StartLocalDate(zone);
    }

    /// <summary>
    /// Not <see cref="ShouldHideTimeLabel"/>, which answers whether a time label would mislead: reading that as
    /// "all day" labels a 22:00–02:00 event all-day on its first day and drops its only time.
    /// </summary>
    internal OccurrenceTimeLabel TimeLabelFor(
        LocalDate day, DateTimeZone zone) =>
        StartLocalDate(zone) != day ? OccurrenceTimeLabel.Continues
        : IsAllDay ? OccurrenceTimeLabel.AllDay
        : OccurrenceTimeLabel.StartTime;

    internal string LocalizedSource(IStringLocalizer<CalendarResource> localizer)
    {
        var label = localizer[$"Calendar_Source_{Source}"];
        return label.ResourceNotFound ? Source : label.Value;
    }

    /// <summary>True for an item from an <c>ICalendarFeedContributor</c>, not Calendar's own events.</summary>
    internal bool IsCommunityContribution() =>
        !string.Equals(Source, CalendarOccurrence.CalendarSource, StringComparison.Ordinal);
}

internal enum OccurrenceTimeLabel
{
    StartTime,

    /// <summary>It covers whole days, and this is the first of them.</summary>
    AllDay,

    /// <summary>It began on an earlier day and runs through this one.</summary>
    Continues,
}
