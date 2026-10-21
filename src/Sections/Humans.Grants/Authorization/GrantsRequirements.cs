using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Grants.Services;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Grants.Authorization;

/// <summary>Admin, Board, or a manager of any program.</summary>
internal sealed class ManagesGrantsRequirement : IAuthorizationRequirement;

/// <summary>
/// The DB-backed policy: the role short-circuit avoids a query on the common admin path.
/// Resolves the live user id, so a merged-away id sees what the surviving account sees.
/// </summary>
internal sealed class GrantsPolicyHandler(IGrantManagement grants) : AuthorizationHandler<ManagesGrantsRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagesGrantsRequirement requirement)
    {
        if (IsAdministering(context.User)
            || (UserId(context.User) is { } id && await grants.ManagesAnyProgramAsync(id)))
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>Admin and Board create programs and name their managers.</summary>
    internal static bool IsAdministering(ClaimsPrincipal user) =>
        user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.Board);

    internal static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
