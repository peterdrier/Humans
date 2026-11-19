using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Humans.Users.Contracts;

namespace Humans.Workgroups.Authorization;

internal sealed class WorkgroupAuthorizationHandler(IUserService users, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<WorkgroupOperationRequirement, WorkgroupInfo>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkgroupOperationRequirement requirement,
        WorkgroupInfo resource)
    {
        var isAdmin = context.User.IsInRole(RoleNames.Admin) || context.User.IsInRole(RoleNames.Board);

        if (requirement == WorkgroupOperationRequirement.Administer
            || (requirement == WorkgroupOperationRequirement.ManageMembership && isAdmin))
        {
            if (isAdmin)
                context.Succeed(requirement);
            return;
        }

        if (requirement == WorkgroupOperationRequirement.Read)
        {
            if (context.User.Identity?.IsAuthenticated == true)
                context.Succeed(requirement);
            return;
        }

        if (!resource.AcceptsMemberWork())
            return;

        if (isAdmin)
        {
            context.Succeed(requirement);
            return;
        }

        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return;
        var user = await users.GetUserInfoAsync(userId,
            httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None);
        if (user is not null && (requirement == WorkgroupOperationRequirement.ManageMembership
                ? resource.IsCoordinator(user.Id)
                : resource.IsMember(user.Id)))
            context.Succeed(requirement);
    }
}
