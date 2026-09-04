using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Models;

internal static class CalendarOccurrenceViewExtensions
{
    public static LocalDate StartLocalDate(this CalendarOccurrence occ, DateTimeZone zone) =>
        occ.OccurrenceStartUtc.InZone(zone).Date;

    public static LocalDate EndLocalDate(this CalendarOccurrence occ, DateTimeZone zone)
    {
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
    /// Which badge a day-per-row view shows for this occurrence on <paramref name="day"/>.
    /// <see cref="ShouldHideTimeLabel"/> answers a different question — whether a time label
    /// would be misleading — and reading it as "is all day" labels a 22:00–02:00 event "all
    /// day" on its first day and drops the only time it has. The two views that render this
    /// badge asked it that way, in duplicate, which is how the defect went unseen.
    /// </summary>
    public static OccurrenceTimeLabel TimeLabelFor(
        this CalendarOccurrence occ, LocalDate day, DateTimeZone zone) =>
        occ.StartLocalDate(zone) != day ? OccurrenceTimeLabel.Continues
        : occ.IsAllDay ? OccurrenceTimeLabel.AllDay
        : OccurrenceTimeLabel.StartTime;
}

internal enum OccurrenceTimeLabel
{
    /// <summary>The occurrence's local start time.</summary>
    StartTime,

    /// <summary>It covers whole days, and this is the first of them.</summary>
    AllDay,

    /// <summary>It began on an earlier day and runs through this one.</summary>
    Continues,
}
