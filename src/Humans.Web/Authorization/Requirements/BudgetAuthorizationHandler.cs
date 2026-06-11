using System.Security.Claims;
using Humans.Application.Interfaces.Budget;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for budget operations.
/// Evaluates whether a user can perform budget operations on a specific BudgetCategory.
///
/// Authorization logic:
/// - Admin: allow any category
/// - FinanceAdmin: allow any category
/// - Department coordinator: allow only categories linked to their department
/// - Everyone else: deny
///
/// Also denies edits on restricted groups and deleted budget years for non-admin users.
/// </summary>
public class BudgetAuthorizationHandler(IBudgetService budgetService)
    : AuthorizationHandler<BudgetOperationRequirement, BudgetCategorySnapshot>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BudgetOperationRequirement requirement,
        BudgetCategorySnapshot resource)
    {
        if (RoleChecks.IsFinanceAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        if (resource.BudgetGroup?.BudgetYear?.IsDeleted == true)
            return;

        if (resource.BudgetGroup?.IsRestricted == true)
            return;

        if (!resource.TeamId.HasValue)
            return;

        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        var coordinatorTeamIds = await budgetService.GetEffectiveCoordinatorTeamIdsAsync(userId);
        if (coordinatorTeamIds.Contains(resource.TeamId.Value))
        {
            context.Succeed(requirement);
        }
    }
}
