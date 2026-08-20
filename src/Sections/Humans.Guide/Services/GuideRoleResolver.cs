using System.Security.Claims;
using Humans.Camps.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Guide.Services;

internal sealed class GuideRoleResolver(
    ITeamServiceRead teamService,
    ICampLeadDirectory campLeadDirectory) : IGuideRoleResolver
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
        var isCampLead = false;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            // Camp lead is a per-camp role, not a claim: the guide's unit of visibility is
            // "leads some camp this year", the same way IsTeamCoordinator is "coordinates
            // some team".
            isCampLead = await campLeadDirectory.IsLeadAnywhereAsync(userId, cancellationToken);

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
            IsCampLead: isCampLead,
            SystemRoles: systemRoles);
    }
}
