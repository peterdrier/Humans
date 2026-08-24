using Humans.Shifts.Contracts;
using NodaTime;

namespace Humans.Events.Helpers;

/// <summary>
/// Shared NodaTime ↔ <see cref="DateTime"/> helpers for Events-section controllers.
/// </summary>
internal static class EventsTimeHelpers
{
    public static DateTimeZone? GetTimeZone(BurnSettingsInfo? burn)
        => burn != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId)
            : null;

    public static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
        => tz == null ? instant.ToDateTimeUtc() : instant.InZone(tz).ToDateTimeUnspecified();

    /// <summary>
    /// The gate-relative day offset of an occurrence. Returns 0 when there is no
    /// gate-opening date to measure from.
    /// </summary>
    public static int ComputeDayOffset(Instant instant, LocalDate? gateOpeningDate, DateTimeZone? tz)
    {
        if (gateOpeningDate == null) return 0;
        var eventDate = tz != null ? instant.InZone(tz).Date : LocalDate.FromDateTime(instant.ToDateTimeUtc());
        return Period.Between(gateOpeningDate.Value, eventDate, PeriodUnits.Days).Days;
    }

    public static Instant ToInstant(DateTime dateTime, DateTimeZone? tz)
    {
        if (tz == null)
            return Instant.FromDateTimeUtc(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
        return LocalDateTime.FromDateTime(dateTime).InZoneLeniently(tz).ToInstant();
    }
}
