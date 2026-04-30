using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Services.Store.Dtos;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for Store order operations.
///
/// Authorization logic (applies to both <see cref="OrderDto"/> resources for
/// View/AddLine/RemoveLine/EditCounterparty and <see cref="StoreOrderCreateContext"/>
/// resources for Create):
/// - Admin or FinanceAdmin: allow any operation
/// - Camp lead/co-lead of the camp owning the resource's CampSeason: allow
/// - Everyone else: deny
/// </summary>
public class StoreOrderAuthorizationHandler : IAuthorizationHandler
{
    private readonly ICampService _campService;

    public StoreOrderAuthorizationHandler(ICampService campService)
    {
        _campService = campService;
    }

    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        // Only react to our own requirement type.
        var pending = context.PendingRequirements
            .OfType<StoreOrderOperationRequirement>()
            .ToList();
        if (pending.Count == 0) return;

        var campSeasonId = context.Resource switch
        {
            OrderDto order => order.CampSeasonId,
            StoreOrderCreateContext create => create.CampSeasonId,
            _ => (Guid?)null
        };
        if (campSeasonId is null) return;

        if (RoleChecks.IsFinanceAdmin(context.User))
        {
            foreach (var req in pending) context.Succeed(req);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var season = await _campService.GetCampSeasonByIdAsync(campSeasonId.Value);
        if (season is null) return;

        if (await _campService.IsUserCampLeadAsync(userId, season.CampId))
        {
            foreach (var req in pending) context.Succeed(req);
        }
    }
}
