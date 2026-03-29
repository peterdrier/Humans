namespace Humans.Domain.Enums;

/// <summary>
/// Whether a budget category represents capital or operational expenditure.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum ExpenditureType
{
    /// <summary>
    /// Capital expenditure — investments, equipment purchases.
    /// </summary>
    CapEx = 0,

    /// <summary>
    /// Operational expenditure — recurring costs.
    /// </summary>
    OpEx = 1
}
