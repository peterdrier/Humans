using Microsoft.AspNetCore.Authorization;

namespace Humans.Expenses.Authorization;

/// <summary>
/// Resource-based authorization requirement for expense report operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, reportDto, requirement)
/// where the resource is an <c>ExpenseReportDto</c>.
/// </summary>
internal sealed class ExpenseReportOperationRequirement(ExpenseReportOperation operation) : IAuthorizationRequirement
{
    public ExpenseReportOperation Operation { get; } = operation;
}
