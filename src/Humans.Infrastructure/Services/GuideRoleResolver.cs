using System.Security.Claims;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Models;
using Humans.Domain.Constants;

namespace Humans.Infrastructure.Services;

public sealed class GuideRoleResolver : IGuideRoleResolver
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

    private readonly ITeamRepository _teamRepository;

    public GuideRoleResolver(ITeamRepository teamRepository)
    {
        _teamRepository = teamRepository;
    }

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
            isCoordinator = await _teamRepository.IsAnyActiveCoordinatorAsync(userId, cancellationToken);
        }

        return new GuideRoleContext(
            IsAuthenticated: true,
            IsTeamCoordinator: isCoordinator,
            SystemRoles: systemRoles);
    }
}
