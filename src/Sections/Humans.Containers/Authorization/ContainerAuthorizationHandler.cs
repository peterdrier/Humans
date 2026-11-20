using Humans.Containers.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.Settings.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Containers.Authorization;

internal sealed class ContainerAuthorizationHandler(
    ICampService campService,
    ICityPlanningService cityPlanningService,
    ISettingsService settingsService,
    IUserService users,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<ContainerOperationRequirement, ContainerAuthorizationTarget>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ContainerOperationRequirement requirement,
        ContainerAuthorizationTarget resource)
    {
        if (CampsRoles.IsCampsAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return;
        }

        var ct = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var viewer = await users.GetUserInfoAsync(userId, ct);
        if (viewer is null) return;

        if (await cityPlanningService.IsCityPlanningTeamMemberAsync(viewer.Id, ct))
        {
            context.Succeed(requirement);
            return;
        }

        // No year and no active event: nothing to authorize.
        var activeYear = settingsService.GetActiveEventSettings() is { Year: > 0 } active ? active.Year : (int?)null;
        if ((resource.Year ?? activeYear) is not { } year)
        {
            return;
        }

        var camp = (await campService.GetCampsForYearAsync(year, ct))
            .FirstOrDefault(c => c.Id == resource.CampId);
        if (camp?.Seasons.Any(s => s.Year == year && s.IsLead(viewer.Id)) != true)
        {
            return;
        }

        if (requirement.Operation == ContainerOperation.Place
            && !await cityPlanningService.IsContainerPlacementOpenAsync(year, ct))
        {
            return;
        }

        context.Succeed(requirement);
    }
}
