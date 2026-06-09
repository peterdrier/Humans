using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Web.Models.Events;
using NodaTime;

namespace Humans.Web.Helpers;

/// <summary>
/// Cross-controller lookup helpers for the Events section. Both id-loops are
/// thin passes over already-cached read-models (UserInfo + CampInfo), so
/// the per-id awaits are dictionary hits in the steady state.
/// </summary>
public static class EventsLookupHelpers
{
    /// <summary>
    /// Builds the day-offset options (one per build/event day from gate opening to
    /// <see cref="BurnSettingsInfo.EventEndOffset"/>) for an event form's date and
    /// recurrence selectors.
    /// </summary>
    public static List<EventDayOptionViewModel> BuildEventDayOptions(BurnSettingsInfo burn)
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

    public static async Task<Dictionary<Guid, UserInfo>> LoadSubmittersAsync(
        IUserServiceRead users, IEnumerable<Guid> userIds)
    {
        var result = new Dictionary<Guid, UserInfo>();
        foreach (var id in userIds)
        {
            var info = await users.GetUserInfoAsync(id);
            if (info != null) result[id] = info;
        }
        return result;
    }

    public static async Task<Dictionary<Guid, CampInfo>> LoadCampsByIdAsync(
        ICampServiceRead camps, int? year)
    {
        if (year is null) return [];
        var list = await camps.GetCampsForYearAsync(year.Value);
        return list.ToDictionary(c => c.Id);
    }
}
