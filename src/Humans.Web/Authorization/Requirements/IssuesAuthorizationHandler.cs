using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for issue operations.
/// Evaluates whether a user can handle (mutate / non-reporter-comment) a specific Issue
/// based on Admin membership or holding any role mapped to the issue's <c>Section</c>
/// via <see cref="IssueSectionRouting"/>.
///
/// Authorization logic for <see cref="IssuesOperationRequirement.Handle"/>:
/// - Admin: allow any issue
/// - Holder of any role in <c>IssueSectionRouting.RolesFor(issue.Section)</c>: allow
/// - Everyone else: deny
///
/// Reads from claims only (RoleAssignmentClaimsTransformation populates them per-request,
/// cached 60s) — no DB hit.
/// </summary>
public class IssuesAuthorizationHandler : AuthorizationHandler<IssuesOperationRequirement, Issue>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IssuesOperationRequirement requirement,
        Issue resource)
    {
        if (context.User.IsInRole(RoleNames.Admin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var sectionRoles = IssueSectionRouting.RolesFor(resource.Section);
        if (sectionRoles.Any(r => context.User.IsInRole(r)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
