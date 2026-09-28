using Microsoft.AspNetCore.Authorization;

namespace Humans.Expenses.Contracts;

/// <summary>
/// Resource-based authorization requirement for expense report operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, reportDto, requirement)
/// where the resource is an <c>ExpenseReportDto</c>. Public so Backdoor's finance read API
/// (peterdrier/Humans#1838) can authorize its report routes the same way <c>ExpensesController</c>
/// does, through <c>IAuthorizationService</c> against the same handler.
/// </summary>
public sealed class ExpenseReportOperationRequirement(ExpenseReportOperation operation) : IAuthorizationRequirement
{
    public ExpenseReportOperation Operation { get; } = operation;
}
