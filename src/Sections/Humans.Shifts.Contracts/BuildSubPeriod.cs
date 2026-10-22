namespace Humans.Shifts.Contracts;

/// <summary>Derived per shift on read, never stored.</summary>
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
