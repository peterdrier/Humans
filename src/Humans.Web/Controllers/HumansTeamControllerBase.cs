using Humans.Application.Interfaces.Teams;
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

    protected async Task<(IActionResult? ErrorResult, User User, TeamInfo Team)> ResolveTeamManagementAsync(string slug)
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

    protected Task<(IActionResult? ErrorResult, User User, TeamInfo Team)> ResolveDepartmentAccessAsync(
        string slug,
        Func<TeamInfo, User, Task<bool>> canAccessAsync)
    {
        return ResolveTeamAccessAsync(
            slug,
            static team => team.SystemTeamType == SystemTeamType.None,
            canAccessAsync);
    }

    private async Task<(IActionResult? ErrorResult, User User, TeamInfo Team)> ResolveTeamAccessAsync(
        string slug,
        Func<TeamInfo, bool> teamFilter,
        Func<TeamInfo, User, Task<bool>> canAccessAsync)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null)
        {
            return (errorResult, null!, null!);
        }

        var normalizedSlug = slug.ToLowerInvariant();
        var teamsById = await _teamService.GetTeamsAsync();
        var team = teamsById.Values.FirstOrDefault(
            t => string.Equals(t.Slug, normalizedSlug, StringComparison.Ordinal)
                 || string.Equals(t.CustomSlug, normalizedSlug, StringComparison.Ordinal));
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
