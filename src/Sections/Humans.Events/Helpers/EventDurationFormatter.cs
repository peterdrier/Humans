using Microsoft.Extensions.Localization;

namespace Humans.Events.Helpers;

/// <summary>Formats event durations with the active culture's short units.</summary>
internal static class EventDurationFormatter
{
    public static string Format(int minutes, IStringLocalizer<EventsResource> localizer)
    {
        var hours = minutes / 60;
        var remainingMinutes = minutes % 60;

        if (hours == 0) return localizer["Events_DurationMin", remainingMinutes].Value;
        return remainingMinutes == 0
            ? localizer["Events_DurationHours", hours].Value
            : localizer["Events_DurationHoursMinutes", hours, remainingMinutes].Value;
    }
}
