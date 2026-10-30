using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Grants.Contracts;
using Humans.Grants.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Grants.Authorization;

internal sealed class ManagesGrantsRequirement : IAuthorizationRequirement, IDescribedRequirement
{
    public IReadOnlyList<string> Admits => [RoleNames.Admin, GrantsRoles.GrantsAdmin, RoleNames.Board, "Grant program managers"];
}

/// <summary>
/// The role short-circuit avoids a query on the common admin path. A merged-away id sees what the surviving account
/// sees.
/// </summary>
internal sealed class GrantsPolicyHandler(IGrantManagement grants, IUserService users) : AuthorizationHandler<ManagesGrantsRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagesGrantsRequirement requirement)
    {
        if (GrantsRoles.ReadsGrants(context.User)
            || (await ViewerAsync(users, context.User) is { } viewer && await grants.ManagesAnyProgramAsync(viewer.Id)))
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>
    /// A cookie issued before an account merge still carries the archived id; this returns the surviving account. The
    /// one place the section reads the viewer's id claim. Null when signed out or unknown.
    /// </summary>
    internal static async ValueTask<UserInfo?> ViewerAsync(IUserService users, ClaimsPrincipal user, CancellationToken ct = default) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await users.GetUserInfoAsync(id, ct)
            : null;
}
