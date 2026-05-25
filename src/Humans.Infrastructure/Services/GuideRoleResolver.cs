using System.Security.Claims;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Models;
using Humans.Domain.Constants;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Services;

public sealed class GuideRoleResolver(ITeamServiceRead teamService) : IGuideRoleResolver
{
    private static readonly IReadOnlyList<string> KnownRoles =
    [
        RoleNames.Admin,
        RoleNames.Board,
        RoleNames.TeamsAdmin,
        RoleNames.CampAdmin,
        RoleNames.TicketAdmin,
        RoleNames.NoInfoAdmin,
        RoleNames.FeedbackAdmin,
        RoleNames.HumanAdmin,
        RoleNames.FinanceAdmin,
        RoleNames.ConsentCoordinator,
        RoleNames.VolunteerCoordinator
    ];

    public async Task<GuideRoleContext> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            return GuideRoleContext.Anonymous;
        }

        var systemRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in KnownRoles)
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
