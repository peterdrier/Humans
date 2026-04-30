using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Services.Store.Dtos;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for Store order operations.
///
/// Authorization logic:
/// - Admin or FinanceAdmin: allow any operation on any order
/// - Camp lead of the camp that owns the order's CampSeason: allow any operation
/// - Everyone else: deny
/// </summary>
public class StoreOrderAuthorizationHandler : AuthorizationHandler<StoreOrderOperationRequirement, OrderDto>
{
    private readonly ICampService _campService;

    public StoreOrderAuthorizationHandler(ICampService campService)
    {
        _campService = campService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StoreOrderOperationRequirement requirement,
        OrderDto resource)
    {
        if (RoleChecks.IsFinanceAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var season = await _campService.GetCampSeasonByIdAsync(resource.CampSeasonId);
        if (season is null) return;

        if (await _campService.IsUserCampLeadAsync(userId, season.CampId))
        {
            context.Succeed(requirement);
        }
    }
}
