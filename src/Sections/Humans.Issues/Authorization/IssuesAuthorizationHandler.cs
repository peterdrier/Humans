using System.Security.Claims;
using Humans.Issues.Domain;
using Humans.Issues.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Issues.Authorization;

/// <summary>
/// The rule itself is <see cref="IssueSectionRouting.CanHandle"/>, which the service also
/// enforces on every per-item read and mutation. This handler is the browser's copy of the
/// same answer, used to shape the page (which controls render, whether a comment may resolve)
/// and to answer 403 before the service would answer 404.
///
/// Reads from claims only (RoleAssignmentClaimsTransformation populates them per-request
/// from in-memory caches) — no DB hit.
/// </summary>
internal sealed class IssuesAuthorizationHandler(IssueSectionRouting routing)
    : AuthorizationHandler<IssuesOperationRequirement, IssueDetail>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IssuesOperationRequirement requirement,
        IssueDetail resource)
    {
        var roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        if (routing.CanHandle(resource.Section, roles))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
