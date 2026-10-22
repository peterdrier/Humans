namespace Humans.Shifts.Contracts;

/// <summary>
/// Set by the coordinator; drives creation (all-day vs time-slotted) and signup (date range vs individual). Stored as
/// a string; distinct from the computed <see cref="ShiftPeriod"/>.
/// </summary>
public enum RotaPeriod
{
    Build = 0,
    Event = 1,
    Strike = 2,
    All = 3
}
