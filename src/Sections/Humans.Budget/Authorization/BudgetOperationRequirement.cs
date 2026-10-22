using Microsoft.AspNetCore.Authorization;

namespace Humans.Budget.Authorization;

internal sealed class BudgetOperationRequirement : IAuthorizationRequirement
{
    public static readonly BudgetOperationRequirement Edit = new(nameof(Edit));

    public string OperationName { get; }

    private BudgetOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
