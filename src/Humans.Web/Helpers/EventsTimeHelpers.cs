using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Web.Helpers;

/// <summary>
/// Shared NodaTime ↔ <see cref="DateTime"/> helpers for Events-section controllers.
/// </summary>
public static class EventsTimeHelpers
{
    public static DateTimeZone? GetTimeZone(EventSettings? eventSettings)
        => eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
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
