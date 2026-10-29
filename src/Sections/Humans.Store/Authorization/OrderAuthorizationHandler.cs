using Humans.Finance.Contracts;
using Humans.Store.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Store.Services;
using Humans.Store.Services.Dtos;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace Humans.Store.Authorization;

/// <summary>
/// Store admins pass first, except billing on team orders, Refund (Admin/FinanceAdmin) and DeletePayment (Admin).
/// The TeamsAdmin path is additive: one who is also a camp lead still gets camp rights through the lead path.
/// Non-admin line edits are denied past the product's OrderableUntil (an <see cref="OrderLineContext"/> resource).
/// </summary>
internal sealed class OrderAuthorizationHandler(
    ICampService campService,
    ITeamService teamService,
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

        if (StoreRoles.CanAdministerStore(context.User))
        {
            foreach (var req in pending)
            {
                // Even admins can't Pay or EditCounterparty a team order: it has no billing.
                if (resource.IsTeamOrder && IsTeamBillingBlocked(req))
                    continue;
                if (req == OrderOperationRequirement.Refund && !FinanceRoles.IsFinanceAdmin(context.User))
                    continue;
                if (req == OrderOperationRequirement.DeletePayment && !RoleChecks.IsAdmin(context.User))
                    continue;
                context.Succeed(req);
            }
            return;
        }

        var pastDeadline = resource.ProductOrderableUntil is { } until
            && pending.Any(IsLineEdit)
            && await settingsService.TodayInEventZoneAsync(clock) > until;

        // Additive: fall through so a TeamsAdmin who is also a camp lead picks up camp rights below.
        if (TeamsRoles.IsTeamsAdmin(context.User))
        {
            foreach (var req in pending)
            {
                if (req == OrderOperationRequirement.View)
                {
                    context.Succeed(req);
                    continue;
                }
                if (!resource.IsTeamOrder) continue;
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

    /// <summary>Only the lead/coordinator block applies this, so a TeamsAdmin still reaches Delete on a team
    /// order.</summary>
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
