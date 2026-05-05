namespace Humans.Domain.Enums;

/// <summary>
/// Computed sub-classification of a Build-period shift, narrowed by day-offset
/// boundaries on EventSettings. NOT stored in DB — derived per shift on read.
/// </summary>
public enum BuildSubPeriod
{
    /// <summary>FirstCrewStartOffset ≤ DayOffset &lt; SetupWeekStartOffset.</summary>
    FirstCrew = 0,

    /// <summary>SetupWeekStartOffset ≤ DayOffset &lt; PreEventWeekStartOffset.</summary>
    SetupWeek = 1,

    /// <summary>PreEventWeekStartOffset ≤ DayOffset &lt; FinishingWeekendStartOffset.</summary>
    PreEventWeek = 2,

    /// <summary>FinishingWeekendStartOffset ≤ DayOffset &lt; 0.</summary>
    FinishingWeekend = 3
}
