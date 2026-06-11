using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

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
/// - Product order deadline: when the resource is a <see cref="StoreOrderLineContext"/>,
///   non-admin line edits are denied past the product's OrderableUntil. Store admins are
///   exempt — they may still add/remove lines past the deadline on an Open order.
/// - Everyone else: deny.
/// </summary>
public class StoreOrderAuthorizationHandler(
    ICampServiceRead campService,
    ITeamServiceRead teamService,
    IShiftManagementService shiftService,
    IClock clock) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        var pending = context.PendingRequirements
            .OfType<StoreOrderOperationRequirement>()
            .ToList();
        if (pending.Count == 0) return;

        if (!TryResolveResource(context.Resource, out var resource))
            return;

        if (RoleChecks.CanAdministerStore(context.User))
        {
            foreach (var req in pending)
            {
                // Even admins can't Pay/EditCounterparty a team order — it has no billing.
                if (resource.IsTeamOrder && IsTeamBillingBlocked(req))
                    continue;
                context.Succeed(req);
            }
            return;
        }

        // Non-admin paths below deny line edits once the product's order deadline has
        // passed — only Store admins (handled above) may cross OrderableUntil.
        var pastDeadline = resource.ProductOrderableUntil is { } until
            && pending.Any(IsLineEdit)
            && await TodayInEventZoneAsync() > until;

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
                if (!resource.IsTeamOrder) continue; // camp orders are view-only for TeamsAdmin
                // Team orders are non-billable — never Pay/EditCounterparty.
                if (IsTeamBillingBlocked(req))
                    continue;
                // Line edits require an Open order, matching the coordinator path and the
                // StoreService guard ("Cannot add/remove lines from an issued order").
                if (IsLineEdit(req) && (!IsOpenOrCreate(resource) || pastDeadline))
                    continue;
                context.Succeed(req);
            }
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        bool authorized = false;
        if (resource.CampSeasonId is { } sid)
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
        else if (resource.TeamId is { } tid)
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
            if (resource.IsTeamOrder && IsTeamBillingBlocked(req))
                continue;

            // Delete is admin-only; camp leads and team coordinators never delete their own orders.
            if (req == StoreOrderOperationRequirement.Delete) continue;

            if (IsMutating(req) && !IsOpenOrCreate(resource))
            {
                continue;
            }
            if (IsLineEdit(req) && pastDeadline)
            {
                continue;
            }
            context.Succeed(req);
        }
    }

    private async Task<LocalDate> TodayInEventZoneAsync()
    {
        var activeEvent = await shiftService.GetActiveAsync();
        var tz = activeEvent is null
            ? DateTimeZone.Utc
            : DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        return clock.GetCurrentInstant().InZone(tz).Date;
    }

    private static bool TryResolveResource(
        object? candidate,
        out StoreOrderAuthorizationResource resource)
    {
        switch (candidate)
        {
            case OrderDto order:
                resource = new StoreOrderAuthorizationResource(
                    order.CampSeasonId,
                    order.TeamId,
                    order.State);
                return true;
            case StoreOrderCreateContext create:
                resource = new StoreOrderAuthorizationResource(
                    create.CampSeasonId,
                    create.TeamId,
                    State: null);
                return true;
            case StoreOrderLineContext line:
                resource = new StoreOrderAuthorizationResource(
                    line.Order.CampSeasonId,
                    line.Order.TeamId,
                    line.Order.State,
                    line.ProductOrderableUntil);
                return true;
            default:
                resource = new StoreOrderAuthorizationResource(null, null, null);
                return false;
        }
    }

    private static bool IsTeamBillingBlocked(StoreOrderOperationRequirement requirement)
        => requirement == StoreOrderOperationRequirement.EditCounterparty
            || requirement == StoreOrderOperationRequirement.Pay;

    private static bool IsLineEdit(StoreOrderOperationRequirement requirement)
        => requirement == StoreOrderOperationRequirement.AddLine
            || requirement == StoreOrderOperationRequirement.RemoveLine;

    private static bool IsMutating(StoreOrderOperationRequirement requirement)
        => IsLineEdit(requirement)
            || requirement == StoreOrderOperationRequirement.EditCounterparty;

    private static bool IsOpenOrCreate(StoreOrderAuthorizationResource resource)
        => resource.State is null or StoreOrderState.Open;

    private sealed record StoreOrderAuthorizationResource(
        Guid? CampSeasonId,
        Guid? TeamId,
        StoreOrderState? State,
        LocalDate? ProductOrderableUntil = null)
    {
        public bool IsTeamOrder => TeamId is not null;
    }
}
