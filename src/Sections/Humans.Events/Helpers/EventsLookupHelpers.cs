using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Models;
using Humans.Events.Services;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Events.Helpers;

/// <summary>
/// Cross-controller lookup helpers for the Events section. Uses the existing Users batch lookup and cached CampInfo projections.
/// </summary>
internal static class EventsLookupHelpers
{
    /// <summary>
    /// Builds the day-offset options (one per build/event day from gate opening to
    /// <see cref="EventSettingsInfo.EventEndOffset"/>) for an event form's date and
    /// recurrence selectors.
    /// </summary>
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

    /// <summary>
    /// A camp's display name for the guide: its active season name, else its slug.
    /// </summary>
    public static string? ResolveCampName(CampInfo? camp) => camp?.Active?.Name ?? camp?.Slug;

    public static async Task<Dictionary<Guid, UserInfo>> LoadSubmittersAsync(
        IUserServiceRead users, IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var infos = await users.GetUserInfosAsync(ids, ct);
        return new Dictionary<Guid, UserInfo>(infos);
    }

    /// <summary>The burn the guide is configured for, or null when the guide is not configured.</summary>
    public static async Task<EventSettingsInfo?> LoadBurnSettingsAsync(
        IEventService guide, EventGuideSettingsView? guideSettings, CancellationToken ct = default)
    {
        if (guideSettings == null) return null;
        return await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId, ct);
    }

    public static async Task<Dictionary<Guid, CampInfo>> LoadCampsByIdAsync(
        ICampServiceRead camps, int? year, CancellationToken ct = default)
    {
        if (year is null) return [];
        var list = await camps.GetCampsForYearAsync(year.Value, ct);
        return list.ToDictionary(c => c.Id);
    }
}
