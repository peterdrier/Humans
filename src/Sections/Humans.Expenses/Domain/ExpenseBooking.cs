namespace Humans.Expenses.Domain;

/// <summary>Where a report books: the Holded account the submitter chose, plus the budget category
/// and year it maps to when the account is a budget category (both null otherwise).</summary>
internal sealed record ExpenseBooking(
    int HoldedAccountNumber,
    string HoldedAccountId,
    Guid? BudgetCategoryId,
    Guid? BudgetYearId);
