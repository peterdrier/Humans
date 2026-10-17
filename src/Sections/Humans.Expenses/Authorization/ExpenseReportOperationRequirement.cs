using Microsoft.AspNetCore.Authorization;

namespace Humans.Expenses.Authorization;

/// <summary>
/// Resource-based authorization requirement for expense report operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, reportDto, requirement)
/// where the resource is an <c>ExpenseReportDto</c>. Internal — an external caller reaches the
/// <c>View</c> check through the <c>PolicyNames.ExpenseReportView</c> named policy this section's
/// <c>SectionPolicies</c> registers, never this type directly.
/// </summary>
internal sealed class ExpenseReportOperationRequirement(ExpenseReportOperation operation) : IAuthorizationRequirement
{
    public ExpenseReportOperation Operation { get; } = operation;
}
