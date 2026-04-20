using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces;
using Humans.Application.Models;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

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

    private readonly HumansDbContext _db;

    public GuideRoleResolver(HumansDbContext db)
    {
        _db = db;
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
            isCoordinator = await _db.TeamMembers
                .AsNoTracking()
                .AnyAsync(
                    tm => tm.UserId == userId
                          && tm.Role == TeamMemberRole.Coordinator
                          && tm.LeftAt == null,
                    cancellationToken);
        }

        return new GuideRoleContext(
            IsAuthenticated: true,
            IsTeamCoordinator: isCoordinator,
            SystemRoles: systemRoles);
    }
}
