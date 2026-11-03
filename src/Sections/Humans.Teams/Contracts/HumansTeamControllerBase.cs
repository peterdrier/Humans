using Humans.Base.Enums;
using Humans.Base.Controllers;
using Humans.Teams.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Teams.Contracts;

/// <summary>
/// Also used by Shifts' <c>ShiftAdminController</c>. Under <c>Contracts/</c>, not the leaf, because it derives from
/// <c>HumansControllerBase</c>, which no <c>.Contracts</c> leaf may reference.
/// </summary>
public abstract class HumansTeamControllerBase(
    IUserService userService,
    ITeamService teamService,
    IAuthorizationService authorizationService) : HumansControllerBase(userService)
{
    protected IAuthorizationService AuthorizationService => authorizationService;

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

    /// <summary>The team's managers, or a Teams reader (Board) who sees the page with <c>CanManage</c> false.</summary>
    protected async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team, bool CanManage)> ResolveTeamViewAsync(
        string slug)
    {
        var canManage = false;
        var (error, user, team) = await ResolveTeamAccessAsync(
            slug,
            static _ => true,
            async (team, _) =>
            {
                canManage = (await authorizationService.AuthorizeAsync(
                    User, team, TeamOperationRequirement.ManageCoordinators)).Succeeded;
                return canManage || (await authorizationService.AuthorizeAsync(User, TeamsPolicies.Read)).Succeeded;
            });
        return (error, user, team, canManage);
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
        Func<TeamInfo, UserInfo, Task<bool>> canAccessAsync,
        CancellationToken ct = default)
    {
        return ResolveTeamAccessAsync(
            slug,
            static team => team.SystemTeamType == SystemTeamType.None,
            canAccessAsync,
            ct);
    }

    private async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveTeamAccessAsync(
        string slug,
        Func<TeamInfo, bool> teamFilter,
        Func<TeamInfo, UserInfo, Task<bool>> canAccessAsync,
        CancellationToken ct = default)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null)
        {
            return (errorResult, null!, null!);
        }

        var normalizedSlug = slug.ToLowerInvariant();
        var teamsById = await teamService.GetTeamsAsync(ct);
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
