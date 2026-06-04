using Humans.Application.Extensions;
using Humans.Web.Controllers;
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


    public static string ToDisplayDate(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayDate();

    public static string? ToDisplayDate(this Instant? value) =>
        value?.ToDisplayDate();

    public static string ToDisplayDateShort(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayDayMonth();

    public static string? ToDisplayDateShort(this Instant? value) =>
        value?.ToDisplayDateShort();

    public static string ToDisplayLongDate(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayLongDate();

    public static string? ToDisplayLongDate(this Instant? value) =>
        value?.ToDisplayLongDate();

    public static string ToDisplayLongDateTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayLongDateTime();

    public static string? ToDisplayLongDateTime(this Instant? value) =>
        value?.ToDisplayLongDateTime();

    public static string ToDisplayDateTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayDateTime();

    public static string? ToDisplayDateTime(this Instant? value) =>
        value?.ToDisplayDateTime();

    public static string ToDisplayCompactDate(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayCompactDate();

    public static string? ToDisplayCompactDate(this Instant? value) =>
        value?.ToDisplayCompactDate();

    public static string ToDisplayCompactDateTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayCompactDateTime();

    public static string? ToDisplayCompactDateTime(this Instant? value) =>
        value?.ToDisplayCompactDateTime();

    public static string ToDisplayCompactDayTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayCompactDayTime();

    public static string ToAuditTimestamp(this Instant value) =>
        value.ToDateTimeUtc().ToAuditTimestamp();

    public static string? ToAuditTimestamp(this Instant? value) =>
        value?.ToAuditTimestamp();

    public static string ToAuditMinuteTimestamp(this Instant value) =>
        value.ToDateTimeUtc().ToAuditMinuteTimestamp();

    public static string? ToAuditMinuteTimestamp(this Instant? value) =>
        value?.ToAuditMinuteTimestamp();
}
