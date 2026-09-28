using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Shifts.Helpers;

/// <summary>Calendar calculations shared by Shifts presentation and service paths.</summary>
internal static class EventCalendarExtensions
{
    internal static LocalDate DateForOffset(this IEventSettingsInfo settings, int dayOffset) =>
        settings.GateOpeningDate.PlusDays(dayOffset);
}
