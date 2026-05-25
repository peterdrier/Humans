using Humans.Application;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansTeamControllerBase(
    IUserServiceRead userService,
    ITeamServiceRead teamService,
    IAuthorizationService authorizationService) : HumansControllerBase(userService)
{
    protected async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveTeamManagementAsync(string slug)
    {
        return await ResolveTeamAccessAsync(
            slug,
            static _ => true,
            async (team, _) =>
            {
                var result = await authorizationService.AuthorizeAsync(
                    User, team, TeamOperationRequirement.ManageCoordinators);
                return result.Succeeded;
            });
    }

    protected Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveDepartmentAccessAsync(
        string slug,
        Func<TeamInfo, UserInfo, Task<bool>> canAccessAsync)
    {
        return ResolveTeamAccessAsync(
            slug,
            static team => team.SystemTeamType == SystemTeamType.None,
            canAccessAsync);
    }

    private async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveTeamAccessAsync(
        string slug,
        Func<TeamInfo, bool> teamFilter,
        Func<TeamInfo, UserInfo, Task<bool>> canAccessAsync)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null)
        {
            return (errorResult, null!, null!);
        }

        var normalizedSlug = slug.ToLowerInvariant();
        var teamsById = await teamService.GetTeamsAsync();
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
