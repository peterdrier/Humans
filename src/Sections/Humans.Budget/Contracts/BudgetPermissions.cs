namespace Humans.Budget.Contracts;

/// <summary>
/// The permission key Budget checks, declared by its <c>Section</c>. Held on a department, it reaches its sub-teams.
/// </summary>
public static class BudgetPermissions
{
    /// <summary>
    /// Edit the line items of the team's budget categories; holding it anywhere opens the department budget pages.
    /// Finance Admin holds it everywhere.
    /// </summary>
    public const string Edit = "Budget.Edit";
}
