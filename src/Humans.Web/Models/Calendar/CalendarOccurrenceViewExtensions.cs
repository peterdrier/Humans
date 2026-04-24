using Humans.Application.DTOs.Calendar;
using NodaTime;

namespace Humans.Web.Models.Calendar;

/// <summary>
/// Presentation helpers for <see cref="CalendarOccurrence"/> — kept in the Web layer
/// because they're display concerns (local-date projection, time-label suppression for
/// all-day or multi-day events).
///
/// The DTO itself stays a pure data record; these extensions compute date ranges in the
/// viewer's timezone for grid rendering.
/// </summary>
public static class CalendarOccurrenceViewExtensions
{
    /// <summary>Local start date in the viewer's timezone.</summary>
    public static LocalDate StartLocalDate(this CalendarOccurrence occ, DateTimeZone zone) =>
        occ.OccurrenceStartUtc.InZone(zone).Date;

    /// <summary>
    /// Inclusive local end date in the viewer's timezone.
    ///
    /// Half-open semantics: the occurrence covers [Start, End). An event whose local end-time
    /// is exactly midnight does NOT cover that following day (standard calendar convention —
    /// 00:00 is the start of the day, not a point belonging to the previous day). We subtract
    /// one tick from the end instant before projecting so that midnight-aligned ends collapse
    /// back onto the previous local date.
    ///
    /// Events with null end are treated as same-day (start date).
    /// </summary>
    public static LocalDate EndLocalDate(this CalendarOccurrence occ, DateTimeZone zone)
    {
        if (occ.OccurrenceEndUtc is not { } endUtc) return occ.StartLocalDate(zone);
        if (endUtc <= occ.OccurrenceStartUtc) return occ.StartLocalDate(zone);

        // Subtract one tick so that a midnight-aligned end (00:00 next day) resolves to the
        // previous day's date — matching how users read "Friday to Sunday, 00:00" on a grid.
        var adjusted = endUtc.Minus(Duration.FromNanoseconds(1));
        var endDate = adjusted.InZone(zone).Date;
        var startDate = occ.StartLocalDate(zone);
        return endDate < startDate ? startDate : endDate;
    }

    /// <summary>
    /// True when the occurrence should be rendered without a leading "HH:mm" time label:
    /// either it's explicitly all-day, or it spans more than one local day.
    /// </summary>
    public static bool ShouldHideTimeLabel(this CalendarOccurrence occ, DateTimeZone zone)
    {
        if (occ.IsAllDay) return true;
        return occ.EndLocalDate(zone) > occ.StartLocalDate(zone);
    }
}
