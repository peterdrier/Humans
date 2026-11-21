using Humans.Base.Authorization;
using Humans.Base.Extensions;
using Humans.Permissions.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Permissions.Authorization;

internal sealed class PermissionHandler(IPermissionService permissions, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<PermissionRequirement, Owner>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement, Owner resource)
    {
        if (context.User.GetUserId() is not { } userId)
            return;

        var ct = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        if (await permissions.HasAsync(userId, requirement.Key, resource, ct))
            context.Succeed(requirement);
    }
}
