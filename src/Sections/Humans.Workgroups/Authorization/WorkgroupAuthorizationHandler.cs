using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Workgroups.Authorization;

internal sealed class WorkgroupAuthorizationHandler
    : AuthorizationHandler<WorkgroupOperationRequirement, WorkgroupInfo>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkgroupOperationRequirement requirement,
        WorkgroupInfo resource)
    {
        var isAdmin = context.User.IsInRole(RoleNames.Admin) || context.User.IsInRole(RoleNames.Board);

        if (requirement == WorkgroupOperationRequirement.Administer)
        {
            if (isAdmin)
                context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (requirement == WorkgroupOperationRequirement.Read)
        {
            if (context.User.Identity?.IsAuthenticated == true)
                context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (!resource.AcceptsMemberWork())
            return Task.CompletedTask;

        if (isAdmin || (Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && resource.IsMember(userId)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
