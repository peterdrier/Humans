using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Teams;
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
/// - TeamsAdmin: View any order (camp or team); manage (AddLine/RemoveLine/Delete)
///   team orders only. Camp orders are view-only. Additive — a TeamsAdmin who is also
///   a camp lead still gets camp-edit rights through the lead path below.
/// - Camp lead/co-lead of the camp owning the resource's CampSeason: allow camp orders.
/// - Coordinator (department-level management role holder) of the resource's Team:
///   allow team orders for View/AddLine/RemoveLine; EditCounterparty and Pay are
///   permanently denied on team orders regardless of role (team orders are non-billable).
///   Mutating operations (AddLine, RemoveLine, EditCounterparty) are gated on
///   order being Open (per Store invariant: "A Camp Lead cannot edit lines or
///   counterparty on an order in InvoiceIssued state").
/// - Everyone else: deny.
/// </summary>
public class StoreOrderAuthorizationHandler(
    ICampServiceRead campService,
    ITeamServiceRead teamService) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        var pending = context.PendingRequirements
            .OfType<StoreOrderOperationRequirement>()
            .ToList();
        if (pending.Count == 0) return;

        Guid? campSeasonId;
        Guid? teamId;
        StoreOrderState? orderState;
        switch (context.Resource)
        {
            case OrderDto order:
                campSeasonId = order.CampSeasonId;
                teamId = order.TeamId;
                orderState = order.State;
                break;
            case StoreOrderCreateContext create:
                campSeasonId = create.CampSeasonId;
                teamId = create.TeamId;
                orderState = null;
                break;
            default:
                return;
        }

        if (RoleChecks.CanAdministerStore(context.User))
        {
            foreach (var req in pending)
            {
                // Even admins can't Pay/EditCounterparty a team order — it has no billing.
                if (teamId is not null &&
                    (req == StoreOrderOperationRequirement.EditCounterparty ||
                     req == StoreOrderOperationRequirement.Pay))
                    continue;
                context.Succeed(req);
            }
            return;
        }

        // TeamsAdmin: read any order; manage team orders only (camp orders stay
        // view-only). Additive — fall through so a TeamsAdmin who is also a camp
        // lead still picks up camp-edit rights in the lead/coordinator block below.
        if (RoleChecks.IsTeamsAdmin(context.User))
        {
            foreach (var req in pending)
            {
                if (req == StoreOrderOperationRequirement.View)
                {
                    context.Succeed(req);
                    continue;
                }
                if (teamId is null) continue; // camp orders are view-only for TeamsAdmin
                // Team orders are non-billable — never Pay/EditCounterparty.
                if (req == StoreOrderOperationRequirement.EditCounterparty ||
                    req == StoreOrderOperationRequirement.Pay)
                    continue;
                // Line edits require an Open order, matching the coordinator path and the
                // StoreService guard ("Cannot add/remove lines from an issued order").
                var isLineEdit = req == StoreOrderOperationRequirement.AddLine
                    || req == StoreOrderOperationRequirement.RemoveLine;
                if (isLineEdit && orderState is not null and not StoreOrderState.Open)
                    continue;
                context.Succeed(req);
            }
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        bool authorized = false;
        if (campSeasonId is { } sid)
        {
            var season = await campService.GetCampSeasonByIdAsync(sid);
            if (season is not null)
            {
                var camp = (await campService.GetCampsForYearAsync(season.Year))
                    .FirstOrDefault(c => c.Id == season.CampId);
                if (camp?.IsLead(userId) == true)
                {
                    authorized = true;
                }
            }
        }
        else if (teamId is { } tid)
        {
            var team = await teamService.GetTeamAsync(tid);
            if (team is not null
                && team.ParentTeamId is null
                && team.ManagementRoleHolderUserIds is not null
                && team.ManagementRoleHolderUserIds.Contains(userId))
                authorized = true;
        }
        if (!authorized) return;

        foreach (var req in pending)
        {
            // Team orders never allow EditCounterparty or Pay regardless of role.
            if (teamId is not null &&
                (req == StoreOrderOperationRequirement.EditCounterparty ||
                 req == StoreOrderOperationRequirement.Pay))
                continue;

            // Delete is admin-only; camp leads and team coordinators never delete their own orders.
            if (req == StoreOrderOperationRequirement.Delete) continue;

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
