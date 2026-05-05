using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for Store order operations.
///
/// Authorization logic (applies to both <see cref="OrderDto"/> resources for
/// View/AddLine/RemoveLine/EditCounterparty and <see cref="StoreOrderCreateContext"/>
/// resources for Create):
/// - Admin or FinanceAdmin: allow any operation regardless of order state.
/// - Camp lead/co-lead of the camp owning the resource's CampSeason: allow.
///   Mutating operations (AddLine, RemoveLine, EditCounterparty) are gated on
///   order being Open (per Store invariant: "A Camp Lead cannot edit lines or
///   counterparty on an order in InvoiceIssued state"). View and Pay carry no
///   state gate — payments continue after invoice issuance.
/// - Everyone else: deny.
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
        var pending = context.PendingRequirements
            .OfType<StoreOrderOperationRequirement>()
            .ToList();
        if (pending.Count == 0) return;

        var (campSeasonId, orderState) = context.Resource switch
        {
            OrderDto order => (order.CampSeasonId, (StoreOrderState?)order.State),
            StoreOrderCreateContext create => (create.CampSeasonId, (StoreOrderState?)null),
            _ => ((Guid?)null, (StoreOrderState?)null)
        };
        if (campSeasonId is null) return;

        if (RoleChecks.CanAdministerStore(context.User))
        {
            foreach (var req in pending) context.Succeed(req);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var season = await _campService.GetCampSeasonByIdAsync(campSeasonId.Value);
        if (season is null) return;

        if (!await _campService.IsUserCampLeadAsync(userId, season.CampId))
            return;

        foreach (var req in pending)
        {
            var isMutating = req == StoreOrderOperationRequirement.AddLine
                || req == StoreOrderOperationRequirement.RemoveLine
                || req == StoreOrderOperationRequirement.EditCounterparty;
            if (isMutating && orderState is not null and not StoreOrderState.Open)
            {
                continue;
            }
            context.Succeed(req);
        }
    }
}
