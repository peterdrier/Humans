namespace Humans.Domain.Enums;

/// <summary>
/// Priority level for shift rotas, affecting urgency scoring.
/// Stored as string in DB.
/// </summary>
public enum ShiftPriority
{
    /// <summary>Standard priority.</summary>
    Normal = 0,

    /// <summary>Higher priority — weighted 3x in urgency scoring.</summary>
    Important = 1,

    /// <summary>Critical priority — weighted 6x in urgency scoring.</summary>
    Essential = 2
}
