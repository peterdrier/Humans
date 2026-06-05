using System.Globalization;
using NodaTime;
using NodaTime.Text;

namespace Humans.Application.Extensions;

public static class DateFormattingExtensions
{
    public static string ToIsoDateString(this LocalDate value) =>
        value.ToString("yyyy-MM-dd", null);

    public static string? ToIsoDateString(this LocalDate? value) =>
        value?.ToIsoDateString();

    public static string ToIsoDateString(this DateTime value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? ToIsoDateString(this DateTime? value) =>
        value?.ToIsoDateString();

    /// <summary>ISO-8601 basic date (no separators), "yyyyMMdd" — DOM-id/slug use.</summary>
    public static string ToBasicIsoDate(this LocalDate value) =>
        value.ToString("yyyyMMdd", null);

    public static string ToInvariantInstantString(this Instant value) =>
        value.ToString(null, CultureInfo.InvariantCulture);

    public static string? ToInvariantInstantString(this Instant? value) =>
        value?.ToInvariantInstantString();

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
    public static string ToDisplayTime(this DateTime value) => value.ToString("HH:mm", CultureInfo.CurrentCulture);
    public static string? ToDisplayTime(this DateTime? value) => value?.ToDisplayTime();
    public static string ToDisplayTime(this LocalTime value) => value.ToString("HH:mm", CultureInfo.CurrentCulture);
    public static string ToDisplayTime(this LocalDateTime value) => value.ToDateTimeUnspecified().ToDisplayTime();

    // 2 — month + day
    public static string ToDisplayMonthDay(this DateTime value) => value.ToString(DisplayDatePattern(false, false), CultureInfo.CurrentCulture);
    public static string? ToDisplayMonthDay(this DateTime? value) => value?.ToDisplayMonthDay();
    public static string ToDisplayMonthDay(this LocalDate value) => value.AtMidnight().ToDateTimeUnspecified().ToDisplayMonthDay();
    public static string? ToDisplayMonthDay(this LocalDate? value) => value?.ToDisplayMonthDay();

    // 3 — month + day + time ("Jun 5 @ 12:34")
    public static string ToDisplayMonthDayTime(this DateTime value) =>
        value.ToString(DisplayDatePattern(false, false) + " '@' HH:mm", CultureInfo.CurrentCulture);
    public static string? ToDisplayMonthDayTime(this DateTime? value) => value?.ToDisplayMonthDayTime();
    public static string ToDisplayMonthDayTime(this LocalDateTime value) => value.ToDateTimeUnspecified().ToDisplayMonthDayTime();

    // 4 — weekday + month + day
    public static string ToDisplayWeekdayDayMonth(this DateTime value) => value.ToString(DisplayDatePattern(true, false), CultureInfo.CurrentCulture);
    public static string? ToDisplayWeekdayDayMonth(this DateTime? value) => value?.ToDisplayWeekdayDayMonth();
    public static string ToDisplayWeekdayDayMonth(this LocalDate value) => value.AtMidnight().ToDateTimeUnspecified().ToDisplayWeekdayDayMonth();
    public static string? ToDisplayWeekdayDayMonth(this LocalDate? value) => value?.ToDisplayWeekdayDayMonth();

    // 5 — weekday + month + day + time
    public static string ToDisplayWeekdayDayMonthTime(this DateTime value) =>
        value.ToString(DisplayDatePattern(true, false) + " HH:mm", CultureInfo.CurrentCulture);
    public static string? ToDisplayWeekdayDayMonthTime(this DateTime? value) => value?.ToDisplayWeekdayDayMonthTime();
    public static string ToDisplayWeekdayDayMonthTime(this LocalDateTime value) => value.ToDateTimeUnspecified().ToDisplayWeekdayDayMonthTime();

    // 6 — date (with year)
    public static string ToDisplayDate(this DateTime value) => value.ToString(DisplayDatePattern(false, true), CultureInfo.CurrentCulture);
    public static string? ToDisplayDate(this DateTime? value) => value?.ToDisplayDate();
    public static string ToDisplayDate(this LocalDate value) => value.AtMidnight().ToDateTimeUnspecified().ToDisplayDate();
    public static string? ToDisplayDate(this LocalDate? value) => value?.ToDisplayDate();

    // 7 — date + time (with year)
    public static string ToDisplayDateTime(this DateTime value) => value.ToString(DisplayDatePattern(false, true) + " HH:mm", CultureInfo.CurrentCulture);
    public static string? ToDisplayDateTime(this DateTime? value) => value?.ToDisplayDateTime();
    public static string ToDisplayDateTime(this LocalDateTime value) => value.ToDateTimeUnspecified().ToDisplayDateTime();

    // Explicit-timezone Instant overloads — render in a SPECIFIED zone (e.g. an event's), not the viewer's session.
    public static string ToDisplayTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayTime();
    public static string ToDisplayMonthDay(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).Date.ToDisplayMonthDay();
    public static string ToDisplayMonthDayTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayMonthDayTime();
    public static string ToDisplayWeekdayDayMonth(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).Date.ToDisplayWeekdayDayMonth();
    public static string ToDisplayWeekdayDayMonthTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayWeekdayDayMonthTime();
    public static string ToDisplayDate(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).Date.ToDisplayDate();
    public static string ToDisplayDateTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayDateTime();

    // niche keepers (distinct shapes outside the 7)
    public static string ToDisplayMonthYear(this DateTime value) => value.ToString("MMM yyyy", CultureInfo.CurrentCulture);
    public static string? ToDisplayMonthYear(this DateTime? value) => value?.ToDisplayMonthYear();
    public static string ToDisplayMonthAbbrev(this LocalDate value) => value.ToString("MMM", CultureInfo.CurrentCulture);
    public static string ToDisplayMonthName(this DateTime value) => value.ToString("MMMM", CultureInfo.CurrentCulture);
    public static string ToTimeWithSeconds(this DateTimeOffset value) => value.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    // --- Audit timestamps ---

    public static string ToAuditTimestamp(this DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ToAuditMinuteTimestamp(this DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // Audit timestamps from an Instant are always UTC (never the user's session tz).
    public static string ToAuditTimestamp(this Instant value) => value.ToDateTimeUtc().ToAuditTimestamp();
    public static string? ToAuditTimestamp(this Instant? value) => value?.ToAuditTimestamp();
    public static string ToAuditMinuteTimestamp(this Instant value) => value.ToDateTimeUtc().ToAuditMinuteTimestamp();
    public static string? ToAuditMinuteTimestamp(this Instant? value) => value?.ToAuditMinuteTimestamp();

    // --- Invariant / machine / interchange ---

    public static string ToInvariantLongDate(this DateTime value) =>
        value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    public static string ToInvariantDayMonthYear(this LocalDate value) =>
        value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    public static string ToIsoDateTimeString(this DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    public static string ToInvariantUtcMinuteLabel(this DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>ISO-8601 UTC instant to seconds, "uuuu-MM-ddTHH:mm:ssZ" (machine/data attributes).</summary>
    public static string ToIso8601(this Instant value) =>
        InstantPattern.General.Format(value);

    /// <summary>Invariant 24-hour time, "HH:mm" with a stable ":" separator (CSV/export columns).</summary>
    public static string ToIsoTime(this DateTime value) =>
        value.ToString("HH:mm", CultureInfo.InvariantCulture);

    // --- Filename / export stamps ---

    public static string ToFileStamp(this DateTime value) =>
        value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public static string ToFileStampMinute(this DateTime value) =>
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
    public static readonly LocalDateTimePattern OpsNoticeDatePattern =
        LocalDateTimePattern.CreateWithInvariantCulture("ddd MMMM d");

    /// <summary>Invariant long date, "MMMM d, yyyy" (email rendering).</summary>
    public static readonly LocalDatePattern InvariantLongDatePattern =
        LocalDatePattern.CreateWithInvariantCulture("MMMM d, yyyy");

    // --- Invariant name-bearing date patterns for CSV/export output (stable English) ---

    /// <summary>Export weekday + day + short month, invariant: "ddd d MMM" (e.g. "Mon 27 May").</summary>
    public static readonly LocalDatePattern InvariantWeekdayDayMonthPattern =
        LocalDatePattern.CreateWithInvariantCulture("ddd d MMM");

    /// <summary>Export short weekday only, invariant: "ddd" (e.g. "Mon").</summary>
    public static readonly LocalDatePattern InvariantWeekdayPattern =
        LocalDatePattern.CreateWithInvariantCulture("ddd");

    /// <summary>Export day + short month, invariant: "d MMM" (e.g. "30 Jun").</summary>
    public static readonly LocalDatePattern InvariantDayMonthPattern =
        LocalDatePattern.CreateWithInvariantCulture("d MMM");

    /// <summary>Export full weekday + day + month + year, invariant: "dddd d MMMM yyyy".</summary>
    public static readonly LocalDatePattern InvariantFullWeekdayDayMonthYearPattern =
        LocalDatePattern.CreateWithInvariantCulture("dddd d MMMM yyyy");

    /// <summary>Export full weekday + day + month, invariant: "dddd d MMMM".</summary>
    public static readonly LocalDatePattern InvariantFullWeekdayDayMonthPattern =
        LocalDatePattern.CreateWithInvariantCulture("dddd d MMMM");
}
