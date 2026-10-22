namespace Humans.Budget.Contracts;

/// <summary>Stored by name: never rename a value.</summary>
public enum BudgetYearStatus
{
    /// <summary>Being built; not visible outside admin.</summary>
    Draft = 0,

    Active = 1,

    /// <summary>Read-only.</summary>
    Closed = 2
}
