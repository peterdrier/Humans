using Humans.Application.DTOs.Calendar;
using NodaTime;

namespace Humans.Web.Models.Calendar;

public static class CalendarOccurrenceViewExtensions
{
    public static LocalDate StartLocalDate(this CalendarOccurrence occ, DateTimeZone zone) =>
        occ.OccurrenceStartUtc.InZone(zone).Date;

    // Inclusive local end date; midnight-aligned ends collapse to the prior day (half-open semantics).
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
}
