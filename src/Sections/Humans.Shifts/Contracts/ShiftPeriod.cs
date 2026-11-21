namespace Humans.Shifts.Contracts;

/// <summary>Derived from DayOffset against the event's offsets, never stored.</summary>
public enum ShiftPeriod
{
    /// <summary>Before gate opening (DayOffset &lt; 0).</summary>
    Build = 0,

    /// <summary>During the event (0 &lt;= DayOffset &lt;= EventEndOffset).</summary>
    Event = 1,

    /// <summary>After event end (DayOffset &gt; EventEndOffset).</summary>
    Strike = 2
}
