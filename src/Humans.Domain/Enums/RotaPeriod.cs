namespace Humans.Domain.Enums;

/// <summary>
/// Explicit period set on a Rota by the coordinator.
/// Drives creation UX (all-day vs time-slotted) and signup UX (date-range vs individual).
/// Stored as string in DB. Distinct from computed <see cref="ShiftPeriod"/>.
/// </summary>
public enum RotaPeriod
{
    Build = 0,
    Event = 1,
    Strike = 2,
    All = 3
}
