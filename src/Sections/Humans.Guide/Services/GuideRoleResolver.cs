using System.Security.Claims;
using Humans.Teams.Contracts;

namespace Humans.Guide.Services;

internal sealed class GuideRoleResolver(ITeamServiceRead teamService) : IGuideRoleResolver
{
    public async Task<GuideRoleContext> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            return GuideRoleContext.Anonymous;
        }

        var systemRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in GuideRolePrivilegeMap.MappedRoles)
        {
            if (user.IsInRole(role))
            {
                systemRoles.Add(role);
            }
        }

        var userIdClaim = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var isCoordinator = false;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            // Answered from the cached TeamInfo snapshot: TeamInfo.Members only
            // contains active (LeftAt is null) memberships, so we just look for
            // any team where the user holds the Coordinator role. Mirrors the
            // SQL filter UserId == userId && Role == Coordinator && LeftAt == null.
            var teamsById = await teamService.GetTeamsAsync(cancellationToken);
            isCoordinator = teamsById.Values.Any(t =>
                t.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator));
        }

        return new GuideRoleContext(
            IsAuthenticated: true,
            IsTeamCoordinator: isCoordinator,
            SystemRoles: systemRoles);
    }
}
