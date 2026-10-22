namespace Humans.Expenses.Domain;

/// <summary>The category and year are set only when the account is a budget category.</summary>
internal sealed record ExpenseBooking(
    int HoldedAccountNumber,
    string? HoldedAccountId,
    Guid? BudgetCategoryId,
    Guid? BudgetYearId);
