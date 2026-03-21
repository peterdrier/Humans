using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
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
        return await ResolveTeamAccessAsync(
            slug,
            static _ => true,
            async (team, user) =>
            {
                if (RoleChecks.IsTeamsAdminBoardOrAdmin(User))
                {
                    return true;
                }

                return await _teamService.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);
            });
    }

    protected Task<(IActionResult? ErrorResult, User User, Team Team)> ResolveDepartmentAccessAsync(
        string slug,
        Func<Team, User, Task<bool>> canAccessAsync)
    {
        return ResolveTeamAccessAsync(
            slug,
            static team => team.ParentTeamId == null && team.SystemTeamType == SystemTeamType.None,
            canAccessAsync);
    }

    private async Task<(IActionResult? ErrorResult, User User, Team Team)> ResolveTeamAccessAsync(
        string slug,
        Func<Team, bool> teamFilter,
        Func<Team, User, Task<bool>> canAccessAsync)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult != null)
        {
            return (errorResult, null!, null!);
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null || !teamFilter(team))
        {
            return (NotFound(), user, null!);
        }

        if (!await canAccessAsync(team, user))
        {
            return (Forbid(), user, team);
        }

        return (null, user, team);
    }
}
