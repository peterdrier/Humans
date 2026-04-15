using Humans.Application.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Authorization handler for budget-wide management operations — resource-free.
///
/// Fires only for <see cref="BudgetOperationRequirement.Manage"/> and succeeds for:
/// - Admin or FinanceAdmin
/// - The system principal (background jobs)
///
/// Everyone else is denied. Paired with the <see cref="BudgetOperationRequirement.Edit"/>
/// handler — that one handles line-item edits with a <see cref="Humans.Domain.Entities.BudgetCategory"/>
/// resource; this one handles admin mutations (budget years, groups, categories,
/// ticketing projections, sync jobs) where no category is in scope.
/// </summary>
public class BudgetManageAuthorizationHandler : AuthorizationHandler<BudgetOperationRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BudgetOperationRequirement requirement)
    {
        if (!ReferenceEquals(requirement, BudgetOperationRequirement.Manage))
            return Task.CompletedTask;

        if (SystemPrincipal.IsSystem(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (RoleChecks.IsFinanceAdmin(context.User))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
