using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization requirement for budget operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, resource, requirement)
/// where the resource is a BudgetCategory.
/// </summary>
public sealed class BudgetOperationRequirement : IAuthorizationRequirement
{
    public static readonly BudgetOperationRequirement Edit = new(nameof(Edit));

    public string OperationName { get; }

    private BudgetOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
