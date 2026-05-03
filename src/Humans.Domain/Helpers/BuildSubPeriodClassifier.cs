using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Domain.Helpers;

/// <summary>
/// Classifies a build-period DayOffset into one of the four sub-periods
/// defined on <see cref="EventSettings"/>. Returns null for offsets outside
/// the build window (≥ 0). Used by the shift dashboard's set-up sub-filter.
/// </summary>
public static class BuildSubPeriodClassifier
{
    public static BuildSubPeriod? Classify(int dayOffset, EventSettings settings)
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
    public static (int StartInclusive, int EndExclusive) BoundsFor(
        BuildSubPeriod subPeriod, EventSettings settings) => subPeriod switch
        {
            BuildSubPeriod.FirstCrew => (settings.FirstCrewStartOffset, settings.SetupWeekStartOffset),
            BuildSubPeriod.SetupWeek => (settings.SetupWeekStartOffset, settings.PreEventWeekStartOffset),
            BuildSubPeriod.PreEventWeek => (settings.PreEventWeekStartOffset, settings.FinishingWeekendStartOffset),
            BuildSubPeriod.FinishingWeekend => (settings.FinishingWeekendStartOffset, 0),
            _ => throw new System.ArgumentOutOfRangeException(nameof(subPeriod)),
        };
}
