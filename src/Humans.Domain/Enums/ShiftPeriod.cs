namespace Humans.Domain.Enums;

/// <summary>
/// Computed classification of a shift's time period relative to the event.
/// NOT stored in DB — derived from DayOffset vs EventSettings offsets.
/// </summary>
public enum ShiftPeriod
{
    /// <summary>Before gate opening (DayOffset &lt; 0).</summary>
    Build = 0,

    /// <summary>During the event (0 &lt;= DayOffset &lt;= EventEndOffset).</summary>
    Event = 1,

    /// <summary>After event end (DayOffset &gt; EventEndOffset).</summary>
    Strike = 2
}
