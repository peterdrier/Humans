namespace Humans.Shifts.Contracts;

/// <summary>Stored as a string.</summary>
public enum ShiftPriority
{
    Normal = 0,

    /// <summary>Weighted 3x in urgency scoring.</summary>
    Important = 1,

    /// <summary>Weighted 6x in urgency scoring.</summary>
    Essential = 2
}
