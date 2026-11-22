namespace Humans.Expenses.Contracts;

/// <summary>
/// The permission key Expenses checks, declared by its <c>Section</c>. Held on a team, it reaches its sub-teams.
/// </summary>
public static class ExpensesPermissions
{
    /// <summary>
    /// Endorse, reject and decline lines on submitted claims against the team's budget categories, and see them.
    /// Finance Admin holds it everywhere.
    /// </summary>
    public const string Endorse = "Expenses.Endorse";
}
