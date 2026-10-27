using Humans.Containers.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Containers.Authorization;

internal sealed class ContainerAuthorizationHandler(ICampService campService, ICityPlanningService cityPlanningService)
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

        if (await cityPlanningService.IsCityPlanningTeamMemberAsync(userId))
        {
            context.Succeed(requirement);
            return;
        }

        // Lead check before the year's settings, so an explicit-year read only runs for a
        // year the caller leads a camp in. No year and no active event: nothing to authorize.
        var settings = resource.Year is null ? await cityPlanningService.GetSettingsAsync() : null;
        if ((resource.Year ?? settings?.Year) is not { } year)
        {
            return;
        }

        var camp = (await campService.GetCampsForYearAsync(year))
            .FirstOrDefault(c => c.Id == resource.CampId);
        if (camp?.Seasons.Any(s => s.Year == year && s.IsLead(userId)) != true)
        {
            return;
        }

        if (requirement.Operation == ContainerOperation.Place)
        {
            settings ??= await cityPlanningService.GetSettingsAsync(year: year);
            if (settings is not { IsContainerPlacementOpen: true })
            {
                return;
            }
        }

        context.Succeed(requirement);
    }
}
