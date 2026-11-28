using Microsoft.AspNetCore.Authorization;

namespace Humans.Budget.Authorization;

internal sealed class BudgetOperationRequirement : IAuthorizationRequirement
{
    public static readonly BudgetOperationRequirement View = new();
    public static readonly BudgetOperationRequirement Edit = new();

    private BudgetOperationRequirement()
    {
    }
}
