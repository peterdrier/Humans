using Humans.Users.Contracts;
using Humans.Camps.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Camps.Authorization;

internal sealed class CampAuthorizationHandler(ICampService campService, IUserService users, IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<CampOperationRequirement>
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

        var ct = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var viewer = await users.GetUserInfoAsync(userId, ct);
        if (viewer is null) return;
        userId = viewer.Id;

        var camp = resourceCamp ?? await GetActiveEventCampAsync(campId.Value, ct);
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

    private async Task<CampInfo?> GetActiveEventCampAsync(Guid campId, CancellationToken ct)
    {
        var activeEventId = (await campService.GetSettingsAsync(ct)).ActiveEventSettingsId;
        return activeEventId is { } eventId
            ? (await campService.GetCampsForEventAsync(eventId, ct)).FirstOrDefault(c => c.Id == campId)
            : null;
    }
}
