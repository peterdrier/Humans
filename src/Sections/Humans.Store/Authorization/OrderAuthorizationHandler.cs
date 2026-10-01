using Humans.Store.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Store.Services.Dtos;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace Humans.Store.Authorization;

/// <summary>
/// Resource-based authorization handler for Store order operations.
///
/// Authorization logic (applies to both <see cref="OrderDto"/> resources for
/// View/AddLine/RemoveLine/EditCounterparty and <see cref="OrderCreateContext"/>
/// resources for Create):
/// - Admin or FinanceAdmin: allow any operation regardless of order state.
/// - TeamsAdmin: View any order (camp or team); manage (Create/AddLine/RemoveLine/Delete)
///   team orders only, for any department, not only the ones they coordinate. Camp orders
///   are view-only. Departments buy from the collective too, and a TeamsAdmin opening the
///   order is how that gets tracked. Additive — a TeamsAdmin who is also a camp lead still
///   gets camp-edit rights through the lead path below.
/// - IssueInvoice and RecordPayment are Store-admin-only on every order, and are additionally
///   denied on team orders even for admins (team orders are non-billable). Refund is narrower
///   still: Admin and FinanceAdmin only — a StoreAdmin is denied. DeletePayment is Admin only. Delete is Store-admin-only on
///   camp orders; on team orders TeamsAdmin gets it too, per the line above.
/// - Camp lead/co-lead of the camp owning the resource's CampSeason: allow camp orders.
/// - Coordinator (department-level management role holder) of the resource's Team:
///   allow team orders for View/AddLine/RemoveLine; EditCounterparty and Pay are
///   permanently denied on team orders regardless of role (team orders are non-billable).
///   Mutating operations (AddLine, RemoveLine, EditCounterparty) are gated on
///   order being Open (per Store invariant: "A Camp Lead cannot edit lines or
///   counterparty on an order in InvoiceIssued state").
/// - Product order deadline: when the resource is a <see cref="OrderLineContext"/>,
///   non-admin line edits are denied past the product's OrderableUntil. Store admins are
///   exempt — they may still add/remove lines past the deadline on an Open order.
/// - Everyone else: deny.
/// </summary>
internal sealed class OrderAuthorizationHandler(
    ICampServiceRead campService,
    ITeamServiceRead teamService,
    ISettingsService settingsService,
    IClock clock) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        var pending = context.PendingRequirements
            .OfType<OrderOperationRequirement>()
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
                if (req == OrderOperationRequirement.Refund && !RoleChecks.IsFinanceAdmin(context.User))
                    continue;
                if (req == OrderOperationRequirement.DeletePayment && !RoleChecks.IsAdmin(context.User))
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
        // view-only) — including Create, for any department rather than only the ones
        // they coordinate. Additive — fall through so a TeamsAdmin who is also a camp
        // lead still picks up camp-edit rights in the lead/coordinator block below.
        if (RoleChecks.IsTeamsAdmin(context.User))
        {
            foreach (var req in pending)
            {
                if (req == OrderOperationRequirement.View)
                {
                    context.Succeed(req);
                    continue;
                }
                if (!resource.IsTeamOrder) continue; // camp orders are view-only for TeamsAdmin
                if (!CanManageNonStoreAdminOrder(req, resource, pastDeadline, canDelete: true))
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
            if (!CanManageNonStoreAdminOrder(req, resource, pastDeadline, canDelete: false))
                continue;
            context.Succeed(req);
        }
    }

    private async Task<LocalDate> TodayInEventZoneAsync()
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync();
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
            case OrderCreateContext create:
                resource = new StoreOrderAuthorizationResource(
                    create.CampSeasonId,
                    create.TeamId,
                    State: null);
                return true;
            case OrderLineContext line:
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

    private static bool IsTeamBillingBlocked(OrderOperationRequirement requirement)
        => requirement == OrderOperationRequirement.EditCounterparty
            || requirement == OrderOperationRequirement.Pay
            || requirement == OrderOperationRequirement.IssueInvoice
            || requirement == OrderOperationRequirement.RecordPayment
            || requirement == OrderOperationRequirement.Refund
            || requirement == OrderOperationRequirement.DeletePayment;

    /// <summary>Operations no camp lead or coordinator ever gets. Consulted from the
    /// lead/coordinator block only — the TeamsAdmin block above does not apply it, so a
    /// TeamsAdmin still reaches Delete on a team order.</summary>
    private static bool IsStoreAdminOnly(OrderOperationRequirement requirement)
        => requirement == OrderOperationRequirement.Delete
            || requirement == OrderOperationRequirement.IssueInvoice
            || requirement == OrderOperationRequirement.RecordPayment
            || requirement == OrderOperationRequirement.Refund
            || requirement == OrderOperationRequirement.DeletePayment;

    private static bool IsLineEdit(OrderOperationRequirement requirement)
        => requirement == OrderOperationRequirement.AddLine
            || requirement == OrderOperationRequirement.RemoveLine;

    private static bool IsMutating(OrderOperationRequirement requirement)
        => IsLineEdit(requirement)
            || requirement == OrderOperationRequirement.EditCounterparty;

    private static bool CanManageNonStoreAdminOrder(
        OrderOperationRequirement requirement,
        StoreOrderAuthorizationResource resource,
        bool pastDeadline,
        bool canDelete)
    {
        if (resource.IsTeamOrder && IsTeamBillingBlocked(requirement)) return false;
        if (!canDelete && IsStoreAdminOnly(requirement)) return false;
        if (IsMutating(requirement) && !IsOpenOrCreate(resource)) return false;
        return !IsLineEdit(requirement) || !pastDeadline;
    }

    private static bool IsOpenOrCreate(StoreOrderAuthorizationResource resource)
        => resource.State is null or OrderState.Open;

    private sealed record StoreOrderAuthorizationResource(
        Guid? CampSeasonId,
        Guid? TeamId,
        OrderState? State,
        LocalDate? ProductOrderableUntil = null)
    {
        public bool IsTeamOrder => TeamId is not null;
    }
}
