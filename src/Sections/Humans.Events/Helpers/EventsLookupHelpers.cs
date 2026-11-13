using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Models;
using Humans.Events.Services;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Events.Helpers;

internal static class EventsLookupHelpers
{
    /// <summary>One option per day from gate opening to <see cref="EventSettingsInfo.EventEndOffset"/>.</summary>
    public static List<EventDayOptionViewModel> BuildEventDayOptions(EventSettingsInfo burn)
    {
        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);
        var days = new List<EventDayOptionViewModel>();
        for (var offset = 0; offset <= burn.EventEndOffset; offset++)
        {
            var date = burn.GateOpeningDate.PlusDays(offset);
            var dt = tz != null
                ? date.AtStartOfDayInZone(tz).ToDateTimeUnspecified()
                : new DateTime(date.Year, date.Month, date.Day, 0, 0, 0);
            days.Add(new EventDayOptionViewModel
            {
                DayOffset = offset,
                Label = date.ToWeekdayDayMonth(),
                Date = dt
            });
        }
        return days;
    }

    /// <summary>The requested season's name or slug; unscoped camp views use the latest season.</summary>
    public static string? ResolveCampName(CampInfo? camp, Guid? eventSettingsId = null) =>
        eventSettingsId is { } eventId
            ? camp?.GetSeasonForEvent(eventId)?.Name ?? camp?.Slug
            : camp?.Active?.Name ?? camp?.Slug;

    public static async Task<Dictionary<Guid, UserInfo>> LoadSubmittersAsync(
        IUserService users, IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var infos = await users.GetUserInfosAsync(ids, ct);
        return new Dictionary<Guid, UserInfo>(infos);
    }

    /// <summary>The record's event when supplied, otherwise the guide's; null when the guide is not configured.</summary>
    public static async Task<EventSettingsInfo?> LoadBurnSettingsAsync(
        IEventManagement guide, EventGuideSettingsView? guideSettings, CancellationToken ct = default,
        Guid? eventSettingsId = null)
    {
        if (guideSettings == null) return null;
        return await guide.GetEventSettingsByIdAsync(eventSettingsId ?? guideSettings.EventSettingsId, ct);
    }

    // Event-scoped CampInfo carries only that season, so Active cannot pick another event's name.
    public static async Task<Dictionary<Guid, CampInfo>> LoadCampsByIdAsync(
        ICampService camps, Guid? eventSettingsId, CancellationToken ct = default)
    {
        if (eventSettingsId is null) return [];
        var list = await camps.GetCampsForEventAsync(eventSettingsId.Value, ct);
        return list.ToDictionary(c => c.Id);
    }
}
