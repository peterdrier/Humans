using Humans.Base.Authorization;
using Humans.Users.Contracts;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Users.Authorization;

/// <summary>Self, or Users Admin / Admin; Board for View only. Service signatures stay auth-free.</summary>
internal sealed class UserEmailAuthorizationHandler(IUserService users, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<UserEmailOperationRequirement, Guid>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        UserEmailOperationRequirement requirement,
        Guid targetUserId)
    {
        if (UsersRoleChecks.IsUsersAdmin(context.User)
            || RoleChecks.IsAdmin(context.User)
            || (requirement == UserEmailOperations.View && RoleChecks.IsBoard(context.User)))
        {
            context.Succeed(requirement);
            return;
        }

        var actorIdRaw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(actorIdRaw, out var actorId)) return;

        var ct = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var actor = await users.GetUserInfoAsync(actorId, ct);
        if (actor is not null && actor.Id == targetUserId)
            context.Succeed(requirement);
    }
}
