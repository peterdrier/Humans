using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Workgroups.Authorization;

/// <summary>
/// Answers the three <see cref="WorkgroupOperationRequirement"/> questions for one group.
/// Membership comes from the group's own roster, which the caller already loaded, so this
/// costs no query; the Board and Admin roles come from claims.
/// </summary>
/// <remarks>
/// The rule that earns its own handler is the freeze: a group that is not
/// <see cref="WorkgroupStatus.Active"/> denies every Member operation, whoever is asking —
/// a dormant group's page is a record, not a workspace. The service enforces the same rule
/// on every write; this is the browser's copy of the answer, so the page renders without
/// buttons that would 403.
/// </remarks>
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
            // Every signed-in human browses the register; anonymous never gets this far.
            if (context.User.Identity?.IsAuthenticated == true)
                context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (!resource.AcceptsMemberWork())
            return Task.CompletedTask;

        if (Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && resource.IsMember(userId))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
