using Humans.Shifts.Contracts;

namespace Humans.Shifts.Services;

/// <summary>Null outside the build window, and before the FirstCrew boundary.</summary>
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

        return null;
    }

    /// <summary>FinishingWeekend ends at 0, when the event begins.</summary>
    internal static (int StartInclusive, int EndExclusive) BoundsFor(
        BuildSubPeriod subPeriod, IBurnSettingsInfo settings) => subPeriod switch
        {
            BuildSubPeriod.FirstCrew => (settings.FirstCrewStartOffset, settings.SetupWeekStartOffset),
            BuildSubPeriod.SetupWeek => (settings.SetupWeekStartOffset, settings.PreEventWeekStartOffset),
            BuildSubPeriod.PreEventWeek => (settings.PreEventWeekStartOffset, settings.FinishingWeekendStartOffset),
            BuildSubPeriod.FinishingWeekend => (settings.FinishingWeekendStartOffset, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(subPeriod)),
        };

    /// <summary>Inclusive bounds. subPeriod narrows only when period is Build.</summary>
    internal static (int? MinDayOffset, int? MaxDayOffset) GetDayOffsetBounds(
        ShiftPeriod? period, BuildSubPeriod? subPeriod, BurnSettingsInfo es)
    {
        int? minDayOffset = null;
        int? maxDayOffset = null;

        switch (period)
        {
            case ShiftPeriod.Build:
                maxDayOffset = -1;
                break;
            case ShiftPeriod.Event:
                minDayOffset = 0;
                maxDayOffset = es.EventEndOffset;
                break;
            case ShiftPeriod.Strike:
                minDayOffset = es.EventEndOffset + 1;
                break;
        }

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            minDayOffset = start;
            // BoundsFor is half-open; repo bounds are inclusive.
            maxDayOffset = end - 1;
        }

        return (minDayOffset, maxDayOffset);
    }
}
