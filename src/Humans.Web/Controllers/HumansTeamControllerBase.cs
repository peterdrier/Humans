using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansTeamControllerBase : HumansControllerBase
{
    private readonly ITeamService _teamService;
    private readonly IAuthorizationService _authorizationService;

    protected HumansTeamControllerBase(
        UserManager<User> userManager,
        ITeamService teamService,
        IAuthorizationService authorizationService)
        : base(userManager)
    {
        _teamService = teamService;
        _authorizationService = authorizationService;
    }

    protected async Task<(IActionResult? ErrorResult, User User, Team Team)> ResolveTeamManagementAsync(string slug)
    {
        return await ResolveTeamAccessAsync(
            slug,
            static _ => true,
            async (team, _) =>
            {
                var result = await _authorizationService.AuthorizeAsync(
                    User, team, TeamOperationRequirement.ManageCoordinators);
                return result.Succeeded;
            });
    }

    protected Task<(IActionResult? ErrorResult, User User, Team Team)> ResolveDepartmentAccessAsync(
        string slug,
        Func<Team, User, Task<bool>> canAccessAsync)
    {
        return ResolveTeamAccessAsync(
            slug,
            static team => team.SystemTeamType == SystemTeamType.None,
            canAccessAsync);
    }

    private async Task<(IActionResult? ErrorResult, User User, Team Team)> ResolveTeamAccessAsync(
        string slug,
        Func<Team, bool> teamFilter,
        Func<Team, User, Task<bool>> canAccessAsync)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null)
        {
            return (errorResult, null!, null!);
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null || !teamFilter(team))
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
