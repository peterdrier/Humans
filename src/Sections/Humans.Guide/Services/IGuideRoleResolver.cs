using System.Security.Claims;

namespace Humans.Guide.Services;

/// <summary>
/// Builds a <see cref="GuideRoleContext"/> for the current user: system roles from
/// claims, team-coordinator status from the cached <c>TeamInfo</c> snapshot via
/// <see cref="Humans.Teams.Contracts.ITeamServiceRead"/>, and camp-lead status via
/// <c>ICampLeadDirectory</c>.
/// </summary>
internal interface IGuideRoleResolver
{
    Task<GuideRoleContext> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}
