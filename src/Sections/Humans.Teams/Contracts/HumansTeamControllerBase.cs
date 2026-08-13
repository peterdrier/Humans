using Humans.Application;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.UI.Controllers;
using Humans.Teams.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Teams.Contracts;

/// <summary>
/// Resolve-a-team-by-slug-then-authorize, shared by this section's admin controller and by
/// Shell's <c>ShiftAdminController</c> (a rota's "department" is a Teams row). Public under
/// <c>Contracts/</c> rather than on the leaf because it derives from <c>HumansControllerBase</c>
/// and returns <c>IActionResult</c> — ASP.NET types the framework-free leaf cannot name, which
/// is exactly the split HUM0034's <c>Contracts</c> carve-out exists for (design §15 step 5b).
/// </summary>
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

    protected async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveEarlyEntryManagementAsync(string slug)
    {
        return await ResolveTeamAccessAsync(
            slug,
            static _ => true,
            async (team, _) =>
            {
                var result = await authorizationService.AuthorizeAsync(
                    User, team, TeamOperationRequirement.ManageEarlyEntry);
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
