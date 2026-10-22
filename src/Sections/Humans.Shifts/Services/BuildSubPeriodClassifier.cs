using Humans.Shifts.Contracts;
using Humans.Settings.Contracts;

namespace Humans.Shifts.Services;

/// <summary>Null outside the build window, and before the FirstCrew boundary.</summary>
internal static class BuildSubPeriodClassifier
{
    internal static BuildSubPeriod? Classify(int dayOffset, IEventSettingsInfo settings)
    {
        if (dayOffset >= 0)
            return null;

        if (dayOffset >= settings.FinishingWeekendStartOffset)
            return BuildSubPeriod.FinishingWeekend;

        if (dayOffset >= settings.PreEventWeekStartOffset)
            return BuildSubPeriod.PreEventWeek;

        if (dayOffset >= settings.SetupWeekStartOffset)
            return BuildSubPeriod.SetupWeek;

        if (dayOffset >= settings.FirstCrewStartOffset)
            return BuildSubPeriod.FirstCrew;

        return null;
    }

    /// <summary>FinishingWeekend ends at 0, when the event begins.</summary>
    internal static (int StartInclusive, int EndExclusive) BoundsFor(
        BuildSubPeriod subPeriod, IEventSettingsInfo settings) => subPeriod switch
        {
            BuildSubPeriod.FirstCrew => (settings.FirstCrewStartOffset, settings.SetupWeekStartOffset),
            BuildSubPeriod.SetupWeek => (settings.SetupWeekStartOffset, settings.PreEventWeekStartOffset),
            BuildSubPeriod.PreEventWeek => (settings.PreEventWeekStartOffset, settings.FinishingWeekendStartOffset),
            BuildSubPeriod.FinishingWeekend => (settings.FinishingWeekendStartOffset, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(subPeriod)),
        };
}
