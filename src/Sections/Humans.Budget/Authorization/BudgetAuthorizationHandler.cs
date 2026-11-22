using Humans.Base.Extensions;
using Humans.Users.Contracts;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;
using Humans.Finance.Contracts;
using Humans.Base.Authorization;
using Humans.Budget.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Budget.Authorization;

internal sealed class BudgetAuthorizationHandler(IPermissionService permissions, IUserService users, IHttpContextAccessor httpContextAccessor)
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
        if (viewing
                ? (await permissions.GetOwnersAsync(viewer.Id, BudgetPermissions.Edit, ct)).Count > 0
                : await permissions.HasAsync(viewer.Id, BudgetPermissions.Edit, new Owner(TeamOwner.Kind, resource.TeamId!.Value), ct))
        {
            context.Succeed(requirement);
        }
    }
}
