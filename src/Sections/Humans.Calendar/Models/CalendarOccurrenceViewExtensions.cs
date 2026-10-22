using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Models;

internal static class CalendarOccurrenceViewExtensions
{
    public static LocalDate StartLocalDate(this CalendarOccurrence occ, DateTimeZone zone) =>
        occ.StartDate ?? occ.OccurrenceStartUtc!.Value.InZone(zone).Date;

    public static LocalDate EndLocalDate(this CalendarOccurrence occ, DateTimeZone zone)
    {
        if (occ.EndDateExclusive is { } endDateExclusive) return endDateExclusive.PlusDays(-1);
        if (occ.OccurrenceEndUtc is not { } endUtc) return occ.StartLocalDate(zone);
        if (endUtc <= occ.OccurrenceStartUtc) return occ.StartLocalDate(zone);

        // -1ns so a midnight-aligned end (00:00 next day) collapses back to the previous local date.
        var adjusted = endUtc.Minus(Duration.FromNanoseconds(1));
        var endDate = adjusted.InZone(zone).Date;
        var startDate = occ.StartLocalDate(zone);
        return endDate < startDate ? startDate : endDate;
    }

    public static bool ShouldHideTimeLabel(this CalendarOccurrence occ, DateTimeZone zone)
    {
        if (occ.IsAllDay) return true;
        return occ.EndLocalDate(zone) > occ.StartLocalDate(zone);
    }

    /// <summary>
    /// Not <see cref="ShouldHideTimeLabel"/>, which answers whether a time label would mislead: reading that as
    /// "all day" labels a 22:00–02:00 event all-day on its first day and drops its only time.
    /// </summary>
    public static OccurrenceTimeLabel TimeLabelFor(
        this CalendarOccurrence occ, LocalDate day, DateTimeZone zone) =>
        occ.StartLocalDate(zone) != day ? OccurrenceTimeLabel.Continues
        : occ.IsAllDay ? OccurrenceTimeLabel.AllDay
        : OccurrenceTimeLabel.StartTime;

    /// <summary>True for an item from an <c>ICalendarFeedContributor</c>, not Calendar's own events.</summary>
    public static bool IsCommunityContribution(this CalendarOccurrence occ) =>
        !string.Equals(occ.Source, CalendarOccurrence.CalendarSource, StringComparison.Ordinal);
}

internal enum OccurrenceTimeLabel
{
    StartTime,

    /// <summary>It covers whole days, and this is the first of them.</summary>
    AllDay,

    /// <summary>It began on an earlier day and runs through this one.</summary>
    Continues,
}
