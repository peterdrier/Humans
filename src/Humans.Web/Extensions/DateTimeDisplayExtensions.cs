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


    public static string ToDisplayTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayTime();
    public static string? ToDisplayTime(this Instant? value) => value?.ToDisplayTime();

    public static string ToDisplayMonthDay(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).Date.ToDisplayMonthDay();
    public static string? ToDisplayMonthDay(this Instant? value) => value?.ToDisplayMonthDay();

    public static string ToDisplayMonthDayTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayMonthDayTime();
    public static string? ToDisplayMonthDayTime(this Instant? value) => value?.ToDisplayMonthDayTime();

    public static string ToDisplayWeekdayDayMonth(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).Date.ToDisplayWeekdayDayMonth();
    public static string? ToDisplayWeekdayDayMonth(this Instant? value) => value?.ToDisplayWeekdayDayMonth();

    public static string ToDisplayWeekdayDayMonthTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayWeekdayDayMonthTime();
    public static string? ToDisplayWeekdayDayMonthTime(this Instant? value) => value?.ToDisplayWeekdayDayMonthTime();

    public static string ToDisplayDate(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).Date.ToDisplayDate();
    public static string? ToDisplayDate(this Instant? value) => value?.ToDisplayDate();

    public static string ToDisplayDateTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayDateTime();
    public static string? ToDisplayDateTime(this Instant? value) => value?.ToDisplayDateTime();

    public static string ToDisplayMonthYear(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayMonthYear();
    public static string? ToDisplayMonthYear(this Instant? value) => value?.ToDisplayMonthYear();
}
