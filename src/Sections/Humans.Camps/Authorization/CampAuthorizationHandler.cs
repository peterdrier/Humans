using Humans.Camps.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps.Authorization;

internal sealed class CampAuthorizationHandler(ICampService campService) : AuthorizationHandler<CampOperationRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CampOperationRequirement requirement)
    {
        var resourceCamp = context.Resource as CampInfo;
        var campId = context.Resource switch
        {
            CampInfo campInfo => campInfo.Id,
            Camp campEntity => campEntity.Id,
            Guid id => id,
            _ => (Guid?)null
        };

        if (campId is null)
            return;

        if (CampsRoles.IsCampsAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        if (requirement.OperationName is not nameof(CampOperationRequirement.Manage)
            and not nameof(CampOperationRequirement.SubmitEvent))
        {
            return;
        }

        var camp = resourceCamp ?? await GetPublicYearCampAsync(campId.Value);
        var allowed = requirement.OperationName switch
        {
            nameof(CampOperationRequirement.Manage) =>
                camp?.IsLead(userId) == true,
            nameof(CampOperationRequirement.SubmitEvent) =>
                camp?.IsEventManager(userId) == true,
            _ => false
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }
    }

    private async Task<CampInfo?> GetPublicYearCampAsync(Guid campId)
    {
        var settings = await campService.GetSettingsAsync();
        return (await campService.GetCampsForYearAsync(settings.PublicYear))
            .FirstOrDefault(c => c.Id == campId);
    }
}
