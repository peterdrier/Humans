using Humans.Finance.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Budget.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Budget.Authorization;

internal sealed class BudgetAuthorizationHandler(IBudgetService budgetService, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<BudgetOperationRequirement, BudgetCategorySnapshot>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BudgetOperationRequirement requirement,
        BudgetCategorySnapshot resource)
    {
        if (FinanceRoles.IsFinanceAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        if (resource.BudgetGroup?.BudgetYear?.IsDeleted == true)
            return;

        if (resource.BudgetGroup?.IsRestricted == true)
            return;

        if (resource.BudgetGroup?.IsTicketingGroup == true)
            return;

        if (!resource.TeamId.HasValue)
            return;

        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        var coordinatorTeamIds = await budgetService.GetEffectiveCoordinatorTeamIdsAsync(
            userId, httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None);
        if (coordinatorTeamIds.Contains(resource.TeamId.Value))
        {
            context.Succeed(requirement);
        }
    }
}
