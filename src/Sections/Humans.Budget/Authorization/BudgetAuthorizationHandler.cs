using Humans.Base.Extensions;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Finance.Contracts;
using Humans.Base.Authorization;
using Humans.Budget.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Budget.Authorization;

internal sealed class BudgetAuthorizationHandler(ITeamService teams, IUserService users, IHttpContextAccessor httpContextAccessor)
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

        if (resource.BudgetGroup?.IsRestricted == true)
            return;

        if (resource.BudgetGroup?.IsTicketingGroup == true)
            return;

        var viewing = requirement == BudgetOperationRequirement.View;
        if (!viewing && (resource.BudgetGroup?.BudgetYear?.IsDeleted == true || !resource.TeamId.HasValue))
            return;

        if (context.User.GetUserId() is not { } userId)
            return;

        var ct = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var viewer = await users.GetUserInfoAsync(userId, ct);
        if (viewer is null) return;
        var coordinatorTeamIds = await teams.GetEffectiveBudgetCoordinatorTeamIdsAsync(viewer.Id, ct);
        if (viewing ? coordinatorTeamIds.Count > 0 : coordinatorTeamIds.Contains(resource.TeamId!.Value))
        {
            context.Succeed(requirement);
        }
    }
}
