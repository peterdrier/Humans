using Humans.Base.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Users.Authorization;

/// <summary>
/// Self-or-admin gate: actor matches target, or actor is Admin / HumanAdmin /
/// Board. Service signatures stay auth-free.
/// </summary>
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

        if (RoleChecks.IsHumanAdminBoardOrAdmin(context.User))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
