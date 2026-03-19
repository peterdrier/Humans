using Humans.Application.Interfaces;
using Humans.Domain.Entities;
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

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage)
        {
            return (Forbid(), user, team);
        }

        return (null, user, team);
    }
}
