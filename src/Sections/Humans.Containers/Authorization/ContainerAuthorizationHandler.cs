using Humans.Containers.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Containers.Authorization;

/// <summary>
/// - Admin / CampAdmin: allow any container
/// - City Planning team member: allow any container
/// - Camp lead: allow only containers belonging to their camp; for
///   <see cref="ContainerOperation.Place"/> the placement phase must also be open in the target year
/// - Everyone else: deny
/// </summary>
internal sealed class ContainerAuthorizationHandler(ICampServiceRead campService, ICityPlanningServiceRead cityPlanningService)
    : AuthorizationHandler<ContainerOperationRequirement, ContainerAuthorizationTarget>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ContainerOperationRequirement requirement,
        ContainerAuthorizationTarget resource)
    {
        if (RoleChecks.IsCampAdmin(context.User))
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

        // Lead check before the year's settings: reading settings creates the year's row,
        // so only a year the caller leads a camp in may reach it.
        var settings = resource.Year is null ? await cityPlanningService.GetSettingsAsync() : null;
        var year = resource.Year ?? settings!.Year;
        var camp = (await campService.GetCampsForYearAsync(year))
            .FirstOrDefault(c => c.Id == resource.CampId);
        if (camp?.Seasons.Any(s => s.Year == year && s.IsLead(userId)) != true)
        {
            return;
        }

        if (requirement.Operation == ContainerOperation.Place)
        {
            settings ??= await cityPlanningService.GetSettingsAsync(year: year);
            if (!settings.IsContainerPlacementOpen)
            {
                return;
            }
        }

        context.Succeed(requirement);
    }
}
