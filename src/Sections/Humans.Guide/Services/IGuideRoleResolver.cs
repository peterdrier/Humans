using System.Security.Claims;

namespace Humans.Guide.Services;

/// <summary>
/// Builds a <see cref="GuideRoleContext"/> for the current user: reads system roles
/// from claims and checks the database for any active team-coordinator assignment.
/// </summary>
internal interface IGuideRoleResolver
{
    Task<GuideRoleContext> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}
