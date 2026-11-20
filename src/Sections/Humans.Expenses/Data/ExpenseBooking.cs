namespace Humans.Expenses.Data;

/// <summary>
/// The account stamp a report write carries. The category and year are set only when the account is a budget category.
/// </summary>
internal sealed record ExpenseBooking(
    int HoldedAccountNumber,
    string? HoldedAccountId,
    Guid? BudgetCategoryId,
    Guid? BudgetYearId);
