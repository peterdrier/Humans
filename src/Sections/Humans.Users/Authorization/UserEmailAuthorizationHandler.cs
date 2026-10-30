using Humans.Base.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Users.Authorization;

/// <summary>Self, or Users Admin / Admin; Board for View only. Service signatures stay auth-free.</summary>
internal sealed class UserEmailAuthorizationHandler
    : AuthorizationHandler<UserEmailOperationRequirement, Guid>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        UserEmailOperationRequirement requirement,
        Guid targetUserId)
    {
        var actorIdRaw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(actorIdRaw, out var actorId) && actorId == targetUserId)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (UsersRoleChecks.IsUsersAdmin(context.User)
            || RoleChecks.IsAdmin(context.User)
            || (requirement == UserEmailOperations.View && RoleChecks.IsBoard(context.User)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
