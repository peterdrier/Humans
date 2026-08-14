using Humans.UI.Authorization;
using System.Security.Claims;
using Humans.Users.Authorization;
using Microsoft.AspNetCore.Authorization;
using Humans.Users.Contracts;

namespace Humans.Users.Authorization;

/// <summary>
/// Resource-based authorization handler for user email operations.
/// Self-or-admin gate: actor matches target user id, or actor is in
/// Admin / HumanAdmin / Board. The HumanAdmin/Board roles match the
/// Profiles section invariants (docs/sections/Profiles.md). Service
/// signatures stay auth-free per design-rules.md.
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
