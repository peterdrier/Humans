using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization requirement for expense report operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, reportDto, requirement)
/// where the resource is an <c>ExpenseReportDto</c>.
/// </summary>
public sealed class ExpenseReportOperationRequirement : IAuthorizationRequirement
{
    public ExpenseReportOperation Operation { get; }

    public ExpenseReportOperationRequirement(ExpenseReportOperation operation)
    {
        Operation = operation;
    }
}
