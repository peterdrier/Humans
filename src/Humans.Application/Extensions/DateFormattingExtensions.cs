using System.Globalization;
using NodaTime;
using NodaTime.Text;

namespace Humans.Application.Extensions;

public static class DateFormattingExtensions
{
    public static string ToInvariantDate(this LocalDate value) =>
        value.ToString("yyyy-MM-dd", null);

    public static string? ToInvariantDate(this LocalDate? value) =>
        value?.ToInvariantDate();

    public static string ToInvariantDate(this DateTime value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? ToInvariantDate(this DateTime? value) =>
        value?.ToInvariantDate();

    // --- Canonical display formats — day/month ORDER and names follow the request culture ---

    // True when the resolved culture writes day before month (es/de/fr/it/ca -> "5 jun");
    // false when month-first (en-US -> "Jun 5"). Read from the live culture, never hard-coded.
    private static bool IsDayFirst(CultureInfo culture)
    {
        foreach (var ch in culture.DateTimeFormat.ShortDatePattern)
        {
            if (ch is 'd' or 'D') return true;
            if (ch is 'M') return false;
        }
        return true;
    }

    private static string DisplayDatePattern(bool weekday, bool year)
    {
        var core = IsDayFirst(CultureInfo.CurrentCulture)
            ? (year ? "d MMM yyyy" : "d MMM")
            : (year ? "MMM d, yyyy" : "MMM d");
        return weekday ? "ddd " + core : core;
    }

    // 1 — time (24h)
    public static string ToTime(this DateTime value) => value.ToString("HH:mm", CultureInfo.CurrentCulture);
    public static string? ToTime(this DateTime? value) => value?.ToTime();
    public static string ToTime(this LocalTime value) => value.ToString("HH:mm", CultureInfo.CurrentCulture);
    public static string ToTime(this LocalDateTime value) => value.ToDateTimeUnspecified().ToTime();

    // 2 — month + day + time ("Jun 5 @ 12:34")
    public static string ToMonthDayTime(this DateTime value) =>
        value.ToString(DisplayDatePattern(false, false) + " '@' HH:mm", CultureInfo.CurrentCulture);
    public static string? ToMonthDayTime(this DateTime? value) => value?.ToMonthDayTime();
    public static string ToMonthDayTime(this LocalDateTime value) => value.ToDateTimeUnspecified().ToMonthDayTime();

    // 3 — weekday + month + day
    public static string ToWeekdayDayMonth(this DateTime value) => value.ToString(DisplayDatePattern(true, false), CultureInfo.CurrentCulture);
    public static string? ToWeekdayDayMonth(this DateTime? value) => value?.ToWeekdayDayMonth();
    public static string ToWeekdayDayMonth(this LocalDate value) => value.AtMidnight().ToDateTimeUnspecified().ToWeekdayDayMonth();
    public static string? ToWeekdayDayMonth(this LocalDate? value) => value?.ToWeekdayDayMonth();

    // 4 — date (with year)
    public static string ToDate(this DateTime value) => value.ToString(DisplayDatePattern(false, true), CultureInfo.CurrentCulture);
    public static string? ToDate(this DateTime? value) => value?.ToDate();
    public static string ToDate(this LocalDate value) => value.AtMidnight().ToDateTimeUnspecified().ToDate();
    public static string? ToDate(this LocalDate? value) => value?.ToDate();

    // 5 — date + time (with year)
    public static string ToDateTime(this DateTime value) => value.ToString(DisplayDatePattern(false, true) + " HH:mm", CultureInfo.CurrentCulture);
    public static string? ToDateTime(this DateTime? value) => value?.ToDateTime();
    public static string ToDateTime(this LocalDateTime value) => value.ToDateTimeUnspecified().ToDateTime();

    // Explicit-timezone Instant overloads — render in a SPECIFIED zone (e.g. an event's), not the viewer's session.
    public static string ToTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToTime();
    public static string ToMonthDayTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToMonthDayTime();
    public static string ToWeekdayDayMonth(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).Date.ToWeekdayDayMonth();
    public static string ToDate(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).Date.ToDate();
    public static string ToDateTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDateTime();

    // niche keepers (distinct shapes outside the 5)
    public static string ToMonthYear(this DateTime value) => value.ToString("MMM yyyy", CultureInfo.CurrentCulture);
    public static string? ToMonthYear(this DateTime? value) => value?.ToMonthYear();
    public static string ToMonthAbbrev(this LocalDate value) => value.ToString("MMM", CultureInfo.CurrentCulture);
    public static string ToMonthName(this DateTime value) => value.ToString("MMMM", CultureInfo.CurrentCulture);
    public static string ToTimeWithSeconds(this DateTimeOffset value) => value.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    // --- Audit timestamps ---

    public static string ToInvariantTimestamp(this DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // Audit timestamps from an Instant are always UTC (never the user's session tz).
    public static string ToInvariantTimestamp(this Instant value) => value.ToDateTimeUtc().ToInvariantTimestamp();
    public static string? ToInvariantTimestamp(this Instant? value) => value?.ToInvariantTimestamp();

    // --- Invariant / machine / interchange ---

    public static string ToInvariantLongDate(this LocalDate value) =>
        value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    public static string ToInvariantLongDate(this DateTime value) =>
        value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>SEPA local date-time, "yyyy-MM-ddTHH:mm:ss" invariant (no trailing Z — SEPA needs none).</summary>
    public static string ToSepaDateTime(this DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>ISO-8601 UTC instant to seconds, "uuuu-MM-ddTHH:mm:ssZ" (machine/data attributes).</summary>
    public static string ToIso8601(this Instant value) =>
        InstantPattern.General.Format(value);

    public static string? ToIso8601(this Instant? value) =>
        value?.ToIso8601();

    /// <summary>Invariant 24-hour time, "HH:mm" with a stable ":" separator (CSV/export columns).</summary>
    public static string ToInvariantTime(this DateTime value) =>
        value.ToString("HH:mm", CultureInfo.InvariantCulture);

    // --- Filename / export stamps ---

    public static string ToFileTimestamp(this DateTime value) =>
        value.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);

    // --- NodaTime patterns: the one sanctioned home for parse/format pattern literals (HUM0030) ---

    /// <summary>iCal basic date-time (RFC 5545 DATE-TIME, e.g. "20260131T142500").</summary>
    public static readonly LocalDateTimePattern IcalBasicDateTimePattern =
        LocalDateTimePattern.CreateWithInvariantCulture("yyyyMMdd'T'HHmmss");

    /// <summary>iCal basic date (RFC 5545 DATE, e.g. "20260131").</summary>
    public static readonly LocalDatePattern IcalBasicDatePattern =
        LocalDatePattern.CreateWithInvariantCulture("yyyyMMdd");

    /// <summary>24-hour time of day, "HH:mm".</summary>
    public static readonly LocalTimePattern TimeOfDayPattern =
        LocalTimePattern.CreateWithInvariantCulture("HH:mm");

    /// <summary>Placement date-time input, "yyyy-MM-ddTHH:mm".</summary>
    public static readonly LocalDateTimePattern PlacementDateTimePattern =
        LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-ddTHH:mm");

    /// <summary>Ops-notice date, "ddd MMMM d" (e.g. "Mon January 5").</summary>
    public static readonly LocalDatePattern OpsNoticeDatePattern =
        LocalDatePattern.CreateWithInvariantCulture("ddd MMMM d");
}
