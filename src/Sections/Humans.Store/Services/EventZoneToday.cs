using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Store.Services;

/// <summary>
/// Today in the active event's time zone (UTC before an event exists). The one source for the product-deadline checks
/// in <see cref="Service"/> and <c>OrderAuthorizationHandler</c>, so they never disagree about the date.
/// </summary>
internal static class EventZoneToday
{
    public static async Task<LocalDate> TodayInEventZoneAsync(this ISettingsService settingsService, IClock clock, CancellationToken ct = default)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        var tz = activeEvent is null
            ? DateTimeZone.Utc
            : DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        return clock.GetCurrentInstant().InZone(tz).Date;
    }
}
