using Humans.Application.Interfaces.Shifts;
using NodaTime;

namespace Humans.Web.Helpers;

/// <summary>
/// Shared NodaTime ↔ <see cref="DateTime"/> helpers for Events-section controllers.
/// </summary>
public static class EventsTimeHelpers
{
    public static DateTimeZone? GetTimeZone(BurnSettingsInfo? burn)
        => burn != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId)
            : null;

    public static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
        => tz == null ? instant.ToDateTimeUtc() : instant.InZone(tz).ToDateTimeUnspecified();

    public static Instant ToInstant(DateTime dateTime, DateTimeZone? tz)
    {
        if (tz == null)
            return Instant.FromDateTimeUtc(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
        return LocalDateTime.FromDateTime(dateTime).InZoneLeniently(tz).ToInstant();
    }
}
