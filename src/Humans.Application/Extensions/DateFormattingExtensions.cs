using System.Globalization;
using NodaTime;
using NodaTime.Text;

namespace Humans.Application.Extensions;

public static class DateFormattingExtensions
{
    /// <summary>
    /// Formats a date as "Wed Jul 1". Mirrors the Web-layer ToDisplayShiftDate() extension.
    /// </summary>
    public static string ToDisplayShiftDate(this LocalDate date) =>
        date.DayOfWeek.ToString()[..3] + " " + date.ToString("MMM d", null);

    public static string ToIsoDateString(this LocalDate value) =>
        value.ToString("yyyy-MM-dd", null);

    public static string? ToIsoDateString(this LocalDate? value) =>
        value?.ToIsoDateString();

    public static string ToIsoDateString(this DateTime value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? ToIsoDateString(this DateTime? value) =>
        value?.ToIsoDateString();

    public static string ToInvariantInstantString(this Instant value) =>
        value.ToString(null, CultureInfo.InvariantCulture);

    public static string? ToInvariantInstantString(this Instant? value) =>
        value?.ToInvariantInstantString();

    // --- Display date ---

    public static string ToDisplayDate(this DateTime value) =>
        value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayDate(this DateTime? value) =>
        value?.ToDisplayDate();

    public static string ToDisplayDate(this LocalDate value) =>
        value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayDate(this LocalDate? value) =>
        value?.ToDisplayDate();

    public static string ToDisplayDate(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).Date.ToDisplayDate();

    // --- Display long date ---

    public static string ToDisplayLongDate(this DateTime value) =>
        value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayLongDate(this DateTime? value) =>
        value?.ToDisplayLongDate();

    // --- Display long date/time ---

    public static string ToDisplayLongDateTime(this DateTime value) =>
        value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayLongDateTime(this DateTime? value) =>
        value?.ToDisplayLongDateTime();

    // --- Display date/time ---

    public static string ToDisplayDateTime(this DateTime value) =>
        value.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayDateTime(this DateTime? value) =>
        value?.ToDisplayDateTime();

    public static string ToDisplayDateTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayDateTime();

    // --- Display compact date ---

    public static string ToDisplayCompactDate(this DateTime value) =>
        value.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayCompactDate(this DateTime? value) =>
        value?.ToDisplayCompactDate();

    public static string ToDisplayCompactDate(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayCompactDate();

    // --- Display compact date/time ---

    public static string ToDisplayCompactDateTime(this DateTime value) =>
        value.ToString("MMM d, yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayCompactDateTime(this DateTime? value) =>
        value?.ToDisplayCompactDateTime();

    public static string ToDisplayCompactDateTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayCompactDateTime();

    // --- Display compact day/time ---

    public static string ToDisplayCompactDayTime(this DateTime value) =>
        value.ToString("MMM d", CultureInfo.CurrentCulture) + " @ " + value.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string ToDisplayCompactDayTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayCompactDayTime();

    // --- Display month/year and day/month ---

    public static string ToDisplayMonthYear(this DateTime value) =>
        value.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayMonthYear(this DateTime? value) =>
        value?.ToDisplayMonthYear();

    public static string ToDisplayDayMonth(this DateTime value) =>
        value.ToString("d MMM", CultureInfo.CurrentCulture);

    public static string? ToDisplayDayMonth(this DateTime? value) =>
        value?.ToDisplayDayMonth();

    // --- Display time ---

    public static string ToDisplayTime(this DateTime value) =>
        value.ToString("H:mm", CultureInfo.InvariantCulture);

    public static string? ToDisplayTime(this DateTime? value) =>
        value?.ToDisplayTime();

    public static string ToDisplayTime(this LocalTime value) =>
        value.ToString("H:mm", null);

    public static string ToDisplayTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToString("H:mm", null);

    // --- Display short date/time (ZonedDateTime) ---

    public static string ToDisplayShortDateTime(this ZonedDateTime value) =>
        value.ToString("ddd MMM d HH:mm", null);

    public static string? ToDisplayShortDateTime(this ZonedDateTime? value) =>
        value?.ToDisplayShortDateTime();

    public static string ToDisplayShortMonthDayTime(this ZonedDateTime value) =>
        value.ToString("MMM d HH:mm", null);

    public static string? ToDisplayShortMonthDayTime(this ZonedDateTime? value) =>
        value?.ToDisplayShortMonthDayTime();

    // --- Audit timestamps ---

    public static string ToAuditTimestamp(this DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ToAuditMinuteTimestamp(this DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // --- General display ---

    public static string ToDisplayGeneralDateTime(this DateTime value) =>
        value.ToString("g", CultureInfo.CurrentCulture);

    // --- Display weekday / month variants ---

    public static string ToDisplayMonthDay(this DateTime value) =>
        value.ToString("MMM d", CultureInfo.CurrentCulture);

    public static string ToDisplayMonthDay(this LocalDate value) =>
        value.ToString("MMM d", null);

    public static string ToDisplayWeekdayDayMonth(this DateTime value) =>
        value.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    public static string ToDisplayFullWeekdayDayMonth(this DateTime value) =>
        value.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

    public static string ToDisplayMonthName(this DateTime value) =>
        value.ToString("MMMM", CultureInfo.CurrentCulture);

    public static string ToDisplayTime24(this DateTime value) =>
        value.ToString("HH:mm", CultureInfo.CurrentCulture);

    // --- Invariant / machine / interchange ---

    public static string ToInvariantLongDate(this DateTime value) =>
        value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    public static string ToIsoDateTimeString(this DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    public static string ToInvariantUtcMinuteLabel(this DateTime value) =>
        value.ToString("uuuu-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

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
}
