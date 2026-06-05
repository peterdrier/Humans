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


    public static string ToTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToTime();
    public static string? ToTime(this Instant? value) => value?.ToTime();

    public static string ToMonthDayTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToMonthDayTime();
    public static string? ToMonthDayTime(this Instant? value) => value?.ToMonthDayTime();

    public static string ToWeekdayDayMonth(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).Date.ToWeekdayDayMonth();
    public static string? ToWeekdayDayMonth(this Instant? value) => value?.ToWeekdayDayMonth();

    public static string ToDate(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).Date.ToDate();
    public static string? ToDate(this Instant? value) => value?.ToDate();

    public static string ToDateTime(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDateTime();
    public static string? ToDateTime(this Instant? value) => value?.ToDateTime();

    public static string ToMonthYear(this Instant value) =>
        value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToMonthYear();
    public static string? ToMonthYear(this Instant? value) => value?.ToMonthYear();
}
