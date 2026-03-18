using System.Globalization;
using NodaTime;

namespace Humans.Web.Extensions;

public static class DateTimeDisplayExtensions
{
    public static string ToDisplayDate(this DateTime value) =>
        value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayDate(this DateTime? value) =>
        value?.ToDisplayDate();

    public static string ToDisplayDate(this Instant value) =>
        value.ToDateTimeUtc().ToDisplayDate();

    public static string? ToDisplayDate(this Instant? value) =>
        value?.ToDisplayDate();

    public static string ToDisplayLongDate(this DateTime value) =>
        value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayLongDate(this DateTime? value) =>
        value?.ToDisplayLongDate();

    public static string ToDisplayLongDate(this Instant value) =>
        value.ToDateTimeUtc().ToDisplayLongDate();

    public static string? ToDisplayLongDate(this Instant? value) =>
        value?.ToDisplayLongDate();

    public static string ToDisplayLongDateTime(this DateTime value) =>
        value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayLongDateTime(this DateTime? value) =>
        value?.ToDisplayLongDateTime();

    public static string ToDisplayLongDateTime(this Instant value) =>
        value.ToDateTimeUtc().ToDisplayLongDateTime();

    public static string? ToDisplayLongDateTime(this Instant? value) =>
        value?.ToDisplayLongDateTime();

    public static string ToDisplayDateTime(this DateTime value) =>
        value.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayDateTime(this DateTime? value) =>
        value?.ToDisplayDateTime();

    public static string ToDisplayDateTime(this Instant value) =>
        value.ToDateTimeUtc().ToDisplayDateTime();

    public static string? ToDisplayDateTime(this Instant? value) =>
        value?.ToDisplayDateTime();

    public static string ToDisplayCompactDate(this DateTime value) =>
        value.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayCompactDate(this DateTime? value) =>
        value?.ToDisplayCompactDate();

    public static string ToDisplayCompactDate(this Instant value) =>
        value.ToDateTimeUtc().ToDisplayCompactDate();

    public static string? ToDisplayCompactDate(this Instant? value) =>
        value?.ToDisplayCompactDate();

    public static string ToDisplayCompactDateTime(this DateTime value) =>
        value.ToString("MMM d, yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayCompactDateTime(this DateTime? value) =>
        value?.ToDisplayCompactDateTime();

    public static string ToDisplayCompactDateTime(this Instant value) =>
        value.ToDateTimeUtc().ToDisplayCompactDateTime();

    public static string? ToDisplayCompactDateTime(this Instant? value) =>
        value?.ToDisplayCompactDateTime();

    public static string ToDisplayShiftDate(this LocalDate value) =>
        value.DayOfWeek.ToString()[..3] + " " + value.ToString("MMM d", null);

    public static string ToDisplayMonthYear(this DateTime value) =>
        value.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayMonthYear(this DateTime? value) =>
        value?.ToDisplayMonthYear();

    public static string ToDisplayDayMonth(this DateTime value) =>
        value.ToString("d MMM", CultureInfo.CurrentCulture);

    public static string? ToDisplayDayMonth(this DateTime? value) =>
        value?.ToDisplayDayMonth();

    public static string ToDisplayTime(this DateTime value) =>
        value.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string? ToDisplayTime(this DateTime? value) =>
        value?.ToDisplayTime();

    public static string ToDisplayTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToString("HH:mm", null);

    public static string ToAuditTimestamp(this DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ToAuditMinuteTimestamp(this DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string ToDisplayGeneralDateTime(this DateTime value) =>
        value.ToString("g", CultureInfo.CurrentCulture);

    public static string ToAuditTimestamp(this Instant value) =>
        value.ToDateTimeUtc().ToAuditTimestamp();

    public static string? ToAuditTimestamp(this Instant? value) =>
        value?.ToAuditTimestamp();

    public static string ToAuditMinuteTimestamp(this Instant value) =>
        value.ToDateTimeUtc().ToAuditMinuteTimestamp();

    public static string? ToAuditMinuteTimestamp(this Instant? value) =>
        value?.ToAuditMinuteTimestamp();
}
