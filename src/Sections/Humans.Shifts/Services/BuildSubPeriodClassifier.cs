using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Humans.Domain.Enums;

namespace Humans.Shifts.Services;

/// <summary>
/// Classifies a build-period DayOffset into one of the four sub-periods
/// defined on <see cref="IBurnSettingsInfo"/>. Returns null for offsets outside
/// the build window (≥ 0). Used by the shift dashboard's set-up sub-filter.
/// </summary>
internal static class BuildSubPeriodClassifier
{
    internal static BuildSubPeriod? Classify(int dayOffset, IBurnSettingsInfo settings)
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

        // Day predates the FirstCrew boundary — unclassified ("pre-build").
        return null;
    }

    /// <summary>
    /// Returns the inclusive start and exclusive end offsets that bracket the
    /// given sub-period. End offset is the next sub-period's start, or 0 for
    /// FinishingWeekend (the final sub-period before the event itself begins).
    /// </summary>
    internal static (int StartInclusive, int EndExclusive) BoundsFor(
        BuildSubPeriod subPeriod, IBurnSettingsInfo settings) => subPeriod switch
        {
            BuildSubPeriod.FirstCrew => (settings.FirstCrewStartOffset, settings.SetupWeekStartOffset),
            BuildSubPeriod.SetupWeek => (settings.SetupWeekStartOffset, settings.PreEventWeekStartOffset),
            BuildSubPeriod.PreEventWeek => (settings.PreEventWeekStartOffset, settings.FinishingWeekendStartOffset),
            BuildSubPeriod.FinishingWeekend => (settings.FinishingWeekendStartOffset, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(subPeriod)),
        };
}
