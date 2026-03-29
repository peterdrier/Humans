namespace Humans.Domain.Enums;

/// <summary>
/// Status of a budget year.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum BudgetYearStatus
{
    /// <summary>
    /// Budget is being built, not visible outside admin.
    /// </summary>
    Draft = 0,

    /// <summary>
    /// Current operational budget.
    /// </summary>
    Active = 1,

    /// <summary>
    /// Year complete, read-only.
    /// </summary>
    Closed = 2
}
