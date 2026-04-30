using System.Security.Claims;
using Humans.Application.Authorization.UserEmail;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization;

/// <summary>
/// Resource-based authorization handler for user email operations.
/// Self-or-admin gate:
/// - Actor whose <see cref="ClaimTypes.NameIdentifier"/> matches the target user id: allow
/// - Actor in the <see cref="RoleNames.Admin"/> role: allow
/// - Everyone else: deny
///
/// Self/admin distinction lives in one handler so the self routes and the
/// admin routes share authorization. Service signatures stay auth-free per
/// design-rules.md.
/// </summary>
public sealed class UserEmailAuthorizationHandler
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

        if (context.User.IsInRole(RoleNames.Admin))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
