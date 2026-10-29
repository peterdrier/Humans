using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Events.Helpers;

internal static class EventsTimeHelpers
{
    public static DateTimeZone? GetTimeZone(EventSettingsInfo? burn)
        => burn != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId)
            : null;

    public static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
        => tz == null ? instant.ToDateTimeUtc() : instant.InZone(tz).ToDateTimeUnspecified();

    /// <summary>0 when there is no gate-opening date to measure from.</summary>
    public static int ComputeDayOffset(Instant instant, LocalDate? gateOpeningDate, DateTimeZone? tz)
    {
        if (gateOpeningDate == null) return 0;
        var eventDate = tz != null ? instant.InZone(tz).Date : LocalDate.FromDateTime(instant.ToDateTimeUtc());
        return Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
    }

    public static bool CanDisplaySchedule(LocalDateTime localStart, int durationMinutes, DateTimeZone? timeZone)
    {
        timeZone ??= DateTimeZone.Utc;
        try
        {
            var start = localStart.InZoneLeniently(timeZone).ToInstant();
            var end = start + Duration.FromMinutes(durationMinutes);
            return start.InUtc().Year >= 1 && start.InZone(timeZone).Year >= 1
                && end.InUtc().Year >= 1 && end.InZone(timeZone).Year >= 1;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static Instant ToInstant(DateTime dateTime, DateTimeZone? tz)
        => ToInstant(LocalDateTime.FromDateTime(dateTime), tz);

    public static Instant ToInstant(LocalDateTime localDateTime, DateTimeZone? tz)
        => tz == null
            ? localDateTime.InUtc().ToInstant()
            : localDateTime.InZoneLeniently(tz).ToInstant();
}
