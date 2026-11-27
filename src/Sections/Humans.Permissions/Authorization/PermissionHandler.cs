using Humans.Base.Authorization;
using Humans.Base.Extensions;
using Humans.Permissions.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Permissions.Authorization;

/// <summary>
/// Checks the surviving account: a cookie can still carry a merged-away id, and holdings and system roles live on the
/// survivor. A missing account grants nothing.
/// </summary>
internal sealed class PermissionHandler(
    IPermissionService permissions,
    IUserService users,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<PermissionRequirement, Owner>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement, Owner resource)
    {
        if (context.User.GetUserId() is not { } cookieId)
            return;

        var ct = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        if (await users.GetUserInfoAsync(cookieId, ct) is not { } user)
            return;

        if (await permissions.HasAsync(user.Id, requirement.Key, resource, ct))
            context.Succeed(requirement);
    }
}
