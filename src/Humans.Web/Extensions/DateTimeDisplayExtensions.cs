using System.Globalization;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using NodaTime;

namespace Humans.Web.Extensions;

public static class DateTimeDisplayExtensions
{
    private static IHttpContextAccessor? _httpContextAccessor;

    /// <summary>
    /// Called once at startup to provide the IHttpContextAccessor.
    /// After this, all parameterless Instant display methods automatically
    /// resolve the user's timezone from the current request session.
    /// </summary>
    public static void Initialize(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Resolves the current user's timezone from session → UTC fallback.
    /// Used internally by parameterless Instant overloads.
    /// </summary>
    private static DateTimeZone GetCurrentUserTimeZone()
    {
        var session = _httpContextAccessor?.HttpContext?.Session;
        if (session is null) return DateTimeZone.Utc;

        var sessionTz = session.GetString(TimezoneApiController.SessionKey);
        if (!string.IsNullOrEmpty(sessionTz))
        {
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(sessionTz);
            if (zone is not null) return zone;
        }

        return DateTimeZone.Utc;
    }

    /// <summary>
    /// Resolves the user's timezone from session, falling back to the event default or UTC.
    /// Fallback chain: session timezone → eventTimeZoneId → UTC.
    /// </summary>
    public static DateTimeZone GetUserTimeZone(this ISession session, string? eventTimeZoneId = null)
    {
        var sessionTz = session.GetString(TimezoneApiController.SessionKey);
        if (!string.IsNullOrEmpty(sessionTz))
        {
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(sessionTz);
            if (zone is not null) return zone;
        }

        if (!string.IsNullOrEmpty(eventTimeZoneId))
        {
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventTimeZoneId);
            if (zone is not null) return zone;
        }

        return DateTimeZone.Utc;
    }

    // --- Timezone-aware Instant overloads (explicit) ---

    public static string ToDisplayDate(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).Date.ToDisplayDate();

    public static string ToDisplayDateTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayDateTime();

    public static string ToDisplayCompactDate(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayCompactDate();

    public static string ToDisplayCompactDateTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayCompactDateTime();

    public static string ToDisplayCompactDayTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToDateTimeUnspecified().ToDisplayCompactDayTime();

    // --- DateTime display methods ---

    public static string ToDisplayDate(this DateTime value) =>
        value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayDate(this DateTime? value) =>
        value?.ToDisplayDate();

    public static string ToDisplayDate(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayDate();

    public static string? ToDisplayDate(this Instant? value) =>
        value?.ToDisplayDate();

    public static string ToDisplayDateShort(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToString("d MMM", CultureInfo.CurrentCulture);

    public static string? ToDisplayDateShort(this Instant? value) =>
        value?.ToDisplayDateShort();

    public static string ToDisplayDate(this LocalDate value) =>
        value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayDate(this LocalDate? value) =>
        value?.ToDisplayDate();

    public static string ToDisplayLongDate(this DateTime value) =>
        value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayLongDate(this DateTime? value) =>
        value?.ToDisplayLongDate();

    public static string ToDisplayLongDate(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayLongDate();

    public static string? ToDisplayLongDate(this Instant? value) =>
        value?.ToDisplayLongDate();

    public static string ToDisplayLongDateTime(this DateTime value) =>
        value.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayLongDateTime(this DateTime? value) =>
        value?.ToDisplayLongDateTime();

    public static string ToDisplayLongDateTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayLongDateTime();

    public static string? ToDisplayLongDateTime(this Instant? value) =>
        value?.ToDisplayLongDateTime();

    public static string ToDisplayDateTime(this DateTime value) =>
        value.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayDateTime(this DateTime? value) =>
        value?.ToDisplayDateTime();

    public static string ToDisplayDateTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayDateTime();

    public static string? ToDisplayDateTime(this Instant? value) =>
        value?.ToDisplayDateTime();

    public static string ToDisplayCompactDate(this DateTime value) =>
        value.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    public static string? ToDisplayCompactDate(this DateTime? value) =>
        value?.ToDisplayCompactDate();

    public static string ToDisplayCompactDate(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayCompactDate();

    public static string? ToDisplayCompactDate(this Instant? value) =>
        value?.ToDisplayCompactDate();

    public static string ToDisplayCompactDateTime(this DateTime value) =>
        value.ToString("MMM d, yyyy HH:mm", CultureInfo.CurrentCulture);

    public static string? ToDisplayCompactDateTime(this DateTime? value) =>
        value?.ToDisplayCompactDateTime();

    public static string ToDisplayCompactDateTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayCompactDateTime();

    public static string? ToDisplayCompactDateTime(this Instant? value) =>
        value?.ToDisplayCompactDateTime();

    public static string ToDisplayCompactDayTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayCompactDayTime();

    public static string ToDisplayCompactDayTime(this DateTime value) =>
        value.ToString("MMM d", CultureInfo.CurrentCulture) + " @ " + value.ToString("HH:mm", CultureInfo.InvariantCulture);

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
        value.ToString("H:mm", CultureInfo.InvariantCulture);

    public static string? ToDisplayTime(this DateTime? value) =>
        value?.ToDisplayTime();

    public static string ToDisplayTime(this Instant value, DateTimeZone timeZone) =>
        value.InZone(timeZone).ToString("H:mm", null);

    public static string ToDisplayTime(this LocalTime value) =>
        value.ToString("H:mm", null);

    public static string ToDisplayShortDateTime(this ZonedDateTime value) =>
        value.ToString("ddd MMM d HH:mm", null);

    public static string? ToDisplayShortDateTime(this ZonedDateTime? value) =>
        value?.ToDisplayShortDateTime();

    public static string ToDisplayShortMonthDayTime(this ZonedDateTime value) =>
        value.ToString("MMM d HH:mm", null);

    public static string? ToDisplayShortMonthDayTime(this ZonedDateTime? value) =>
        value?.ToDisplayShortMonthDayTime();

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
