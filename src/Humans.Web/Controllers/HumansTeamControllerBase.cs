using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansTeamControllerBase : HumansControllerBase
{
    private readonly ITeamService _teamService;

    protected HumansTeamControllerBase(UserManager<User> userManager, ITeamService teamService)
        : base(userManager)
    {
        _teamService = teamService;
    }

    protected async Task<(IActionResult? ErrorResult, User User, Team Team)> ResolveTeamManagementAsync(string slug)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult != null)
        {
            return (errorResult, null!, null!);
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return (NotFound(), user, null!);
        }

        // Claims-first: global roles grant access to all teams
        if (!RoleChecks.IsTeamsAdminBoardOrAdmin(User))
        {
            // Fall back to team-specific coordinator check (requires DB)
            var isCoordinator = await _teamService.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);
            if (!isCoordinator)
            {
                return (Forbid(), user, team);
            }
        }

        return (null, user, team);
    }
}
