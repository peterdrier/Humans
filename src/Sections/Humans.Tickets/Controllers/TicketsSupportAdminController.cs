using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Tickets.Controllers;

/// <summary>
/// The ticketing team's customer service pages over the checkout lane: lookup, refund list, resale queue, report, the
/// ticket exceptions (rename, void and refund, free companion), the audited overrides, and cancelling an event. English
/// only.
/// </summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets/Admin/Sales")]
internal sealed class TicketsSupportAdminController(
    ITicketCheckoutService checkout,
    ISettingsService settings,
    IClock clock,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("Lookup")]
    public async Task<IActionResult> Lookup(string? q, CancellationToken ct)
    {
        var query = q?.Trim() ?? string.Empty;
        var hits = new List<OrderSearchHit>();
        if (query.Length >= TicketSupportReports.MinimumQueryLength)
        {
            foreach (var salesEvent in await checkout.GetSalesEventsAsync(ct))
            {
                var eventName = await EventNameAsync(salesEvent, ct);
                var tickets = (await checkout.GetTicketsForSalesEventAsync(salesEvent.Id, ct)).ToLookup(t => t.OrderId);
                hits.AddRange((await checkout.GetOrdersForSalesEventAsync(salesEvent.Id, ct))
                    .Where(o => TicketSupportReports.Matches(o, tickets[o.Id], query))
                    .Select(o => new OrderSearchHit(o, tickets[o.Id].ToList(), eventName)));
            }
        }
        var newest = hits.OrderByDescending(h => h.Order.CreatedAt).Take(TicketSupportReports.MaxHits).ToList();
        return View("~/Views/Tickets/Admin/Sales/Lookup.cshtml", new LookupViewModel(query, newest));
    }

    [HttpGet("{id:guid}/Refunds")]
    public async Task<IActionResult> Refunds(Guid id, RefundState? state, CancellationToken ct)
    {
        var salesEvent = await checkout.GetSalesEventAsync(id, ct);
        if (salesEvent is null)
            return NotFound();
        var rows = TicketSupportReports.RefundList(
            await checkout.GetOrdersForSalesEventAsync(id, ct), await checkout.GetRefundsForSalesEventAsync(id, ct), state);
        return View("~/Views/Tickets/Admin/Sales/Refunds.cshtml",
            new RefundsViewModel(id, await EventNameAsync(salesEvent, ct), state, rows));
    }

    [HttpGet("{id:guid}/Resale")]
    public async Task<IActionResult> Resale(Guid id, CancellationToken ct)
    {
        var salesEvent = await checkout.GetSalesEventAsync(id, ct);
        if (salesEvent is null)
            return NotFound();
        var tickets = await checkout.GetTicketsForSalesEventAsync(id, ct);
        var rows = TicketSupportReports.ResaleQueue(
            await checkout.GetOffersForSalesEventAsync(id, ct), tickets, await checkout.GetRefundsForSalesEventAsync(id, ct));
        var reservedBy = (await checkout.GetOrdersForSalesEventAsync(id, ct)).ToDictionary(o => o.Id);
        return View("~/Views/Tickets/Admin/Sales/Resale.cshtml", new ResaleViewModel(
            id, await EventNameAsync(salesEvent, ct), salesEvent, rows, reservedBy, clock.GetCurrentInstant()));
    }

    [HttpPost("{id:guid}/Resale/{offerId:guid}/Withdraw")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawOffer(Guid id, Guid offerId, string? reason, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await checkout.WithdrawOfferForHolderAsync(offerId, reason ?? string.Empty, user.Id, ct);
        if (result.Succeeded)
            SetSuccess("Taken out of the queue: the holder keeps the ticket.");
        else
            SetError(result.Refusal!);
        return RedirectToAction(nameof(Resale), new { id });
    }

    [HttpGet("{id:guid}/Cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var salesEvent = await checkout.GetSalesEventAsync(id, ct);
        var progress = await checkout.GetCancellationProgressAsync(id, ct);
        if (salesEvent is null || progress is null)
            return NotFound();
        return View("~/Views/Tickets/Admin/Sales/Cancel.cshtml", new CancelViewModel(
            id, await EventNameAsync(salesEvent, ct), salesEvent.BackendKey, progress));
    }

    [HttpPost("{id:guid}/Cancel")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, string? reason, string? confirmName, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;
        var salesEvent = await checkout.GetSalesEventAsync(id, ct);
        if (salesEvent is null)
            return NotFound();

        if (!string.Equals(confirmName?.Trim(), await EventNameAsync(salesEvent, ct), StringComparison.Ordinal))
            SetError("Type the event's name exactly to confirm. Nothing was cancelled.");
        else if (await checkout.CancelSalesEventAsync(id, reason ?? string.Empty, user.Id, ct) is { Succeeded: false } refused)
            SetError(refused.Refusal!);
        else
            SetSuccess("Event cancelled. Sales are closed; tickets are voided and orders refunded over the next runs, every five minutes.");
        return RedirectToAction(nameof(Cancel), new { id });
    }

    [HttpPost("{id:guid}/Resale/Open")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetResaleOpen(Guid id, bool open, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await checkout.SetResaleOpenAsync(id, open, user.Id, ct);
        if (result.Succeeded)
            SetSuccess(open ? "Resale is open." : "Resale is paused.");
        else
            SetError(result.Refusal!);
        return RedirectToAction(nameof(Resale), new { id });
    }

    [HttpGet("{id:guid}/Companions")]
    public async Task<IActionResult> Companions(Guid id, CancellationToken ct)
    {
        var salesEvent = await checkout.GetSalesEventAsync(id, ct);
        if (salesEvent is null)
            return NotFound();
        return View("~/Views/Tickets/Admin/Sales/Companions.cshtml", new CompanionsViewModel(
            id, await EventNameAsync(salesEvent, ct), await checkout.GetCompanionRequestsAsync(id, ct)));
    }

    [HttpPost("{id:guid}/Companions/{requestId:guid}")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideCompanion(Guid id, Guid requestId, bool approve, string? note, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await checkout.DecideCompanionRequestAsync(requestId, approve, note, user.Id, ct);
        if (result.Succeeded)
            SetSuccess(approve ? "Approved: the companion ticket is issued and emailed." : "Declined: the holder sees your note.");
        else
            SetError(result.Refusal!);
        return RedirectToAction(nameof(Companions), new { id });
    }

    [HttpGet("{id:guid}/Report")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        var salesEvent = await checkout.GetSalesEventAsync(id, ct);
        if (salesEvent is null)
            return NotFound();
        var evt = await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct);
        var report = TicketSupportReports.Sales(
            await checkout.GetOrdersForSalesEventAsync(id, ct),
            await checkout.GetTicketsForSalesEventAsync(id, ct),
            await checkout.GetRefundsForSalesEventAsync(id, ct),
            await checkout.GetOffersForSalesEventAsync(id, ct),
            evt?.TimeZone ?? DateTimeZone.Utc);
        return View("~/Views/Tickets/Admin/Sales/Report.cshtml",
            new ReportViewModel(id, evt?.EventName ?? salesEvent.EventSettingsId.ToString(), salesEvent.Capacity, report));
    }

    [HttpGet("Order/{orderId:guid}/Ticket/{ticketId:guid}")]
    public async Task<IActionResult> Ticket(Guid orderId, Guid ticketId, CancellationToken ct)
    {
        var detail = await checkout.GetOrderDetailAsync(orderId, ct);
        var ticket = detail?.Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (detail is null || ticket is null)
            return NotFound();
        var eventTickets = await checkout.GetTicketsForSalesEventAsync(ticket.SalesEventId, ct);
        var offer = (await checkout.GetOffersForSalesEventAsync(ticket.SalesEventId, ct))
            .Where(o => o.TicketId == ticket.Id).MaxBy(o => o.OfferedAt);
        var dependents = eventTickets.Where(t => t.ParentTicketId == ticket.Id).ToList();
        return View("~/Views/Tickets/Admin/Sales/Ticket.cshtml", new TicketAdminViewModel(
            detail.Order, ticket,
            ticket.ParentTicketId is { } parentId ? eventTickets.FirstOrDefault(t => t.Id == parentId) : null,
            dependents,
            offer,
            detail.VoidRefundMinor(ticket),
            detail.RefundableMinor));
    }

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/Rename")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Rename(Guid orderId, Guid ticketId, string? holderName, string? holderEmail, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Ticket renamed.", (actor, c) =>
            checkout.RenameTicketAsync(ticketId, holderName ?? string.Empty, holderEmail ?? string.Empty, reason ?? string.Empty,
                overrideRules: false, actor, c), ct);

    /// <summary>
    /// Refunds <paramref name="refund"/> (euros) and voids, or voids with no refund when <paramref name="noRefund"/> is
    /// chosen; the form makes the team pick one, so a void never skips the money by accident.
    /// </summary>
    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/Void")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Void(Guid orderId, Guid ticketId, string? reason, decimal? refund, bool noRefund, CancellationToken ct)
    {
        var refundMinor = noRefund || refund is not { } euros ? 0 : (long)Math.Round(euros * 100m, MidpointRounding.AwayFromZero);
        if (!noRefund && refundMinor <= 0)
        {
            SetError("Enter the amount to refund, or choose 'No refund' to void without one. Nothing was voided.");
            return RedirectToAction(nameof(Ticket), new { orderId, ticketId });
        }
        return await ActAsync(orderId, ticketId,
            noRefund ? "Ticket voided with no refund." : "Ticket voided and the refund sent; it shows on the order and the refund list.",
            (actor, c) => checkout.VoidTicketAsync(ticketId, reason ?? string.Empty, refundMinor, actor, c), ct);
    }

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/Restore")]
    [Authorize(Policy = TicketsPolicies.Override)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Restore(Guid orderId, Guid ticketId, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Override: ticket restored.", (actor, c) =>
            checkout.RestoreTicketAsync(ticketId, reason ?? string.Empty, actor, c), ct);

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/OverrideRename")]
    [Authorize(Policy = TicketsPolicies.Override)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> OverrideRename(Guid orderId, Guid ticketId, string? holderName, string? holderEmail, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Override: ticket renamed.", (actor, c) =>
            checkout.RenameTicketAsync(ticketId, holderName ?? string.Empty, holderEmail ?? string.Empty, reason ?? string.Empty,
                overrideRules: true, actor, c), ct);

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/OverrideCompanion")]
    [Authorize(Policy = TicketsPolicies.Override)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> OverrideCompanion(Guid orderId, Guid ticketId, string? holderName, string? holderEmail, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Override: companion ticket issued.", (actor, c) =>
            checkout.IssueCompanionAsync(ticketId, holderName ?? string.Empty, holderEmail, reason ?? string.Empty,
                overrideCapacity: true, actor, c), ct);

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/Companion")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Companion(Guid orderId, Guid ticketId, string? holderName, string? holderEmail, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Companion ticket issued.", (actor, c) =>
            checkout.IssueCompanionAsync(ticketId, holderName ?? string.Empty, holderEmail, reason ?? string.Empty,
                overrideCapacity: false, actor, c), ct);

    private async Task<IActionResult> ActAsync(
        Guid orderId, Guid ticketId, string success, Func<Guid, CancellationToken, Task<CheckoutMutationResult>> act, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await act(user.Id, ct);
        if (result.Succeeded)
            SetSuccess(success);
        else
            SetError(result.Refusal!);
        return RedirectToAction(nameof(Ticket), new { orderId, ticketId });
    }

    private async Task<string> EventNameAsync(TicketSalesEvent salesEvent, CancellationToken ct) =>
        (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct))?.EventName ?? salesEvent.EventSettingsId.ToString();
}

internal sealed record OrderSearchHit(TicketCheckoutOrder Order, IReadOnlyList<TicketCheckoutTicket> Tickets, string EventName);

internal sealed record LookupViewModel(string Query, IReadOnlyList<OrderSearchHit> Hits);

internal sealed record RefundsViewModel(Guid SalesEventId, string EventName, RefundState? State, IReadOnlyList<RefundListRow> Rows);

/// <summary>The resale queue page. <c>Orders</c> holds the event's orders by id, to name the checkout holding a reserved ticket.</summary>
internal sealed record ResaleViewModel(
    Guid SalesEventId, string EventName, TicketSalesEvent SalesEvent, IReadOnlyList<ResaleQueueRow> Rows,
    IReadOnlyDictionary<Guid, TicketCheckoutOrder> Orders, Instant Now)
{
    /// <summary>A reserved offer whose checkout is over or past its hold, which the reconcile job has not yet put right.</summary>
    public bool IsStuck(TicketResaleOffer offer) =>
        offer is { State: TicketResaleOfferState.Reserved, ReservedByOrderId: { } holdingId }
        && (Orders.GetValueOrDefault(holdingId) is not { IsOpen: true } holding || holding.ExpiresAt < Now);

    public TicketSalesMode Mode => SalesEvent.Mode;
    public bool ResaleSelling => SalesEvent.ResaleSelling;
    public bool SwitchedOff => SalesEvent.EffectiveSwitches.Resale == ResaleScheme.Off;
}

internal sealed record CompanionsViewModel(Guid SalesEventId, string EventName, IReadOnlyList<CompanionRequestRow> Rows);

internal sealed record ReportViewModel(Guid SalesEventId, string EventName, int Capacity, SalesReport Report);

/// <summary>
/// One ticket's admin page. <c>SuggestedRefundMinor</c> is what voiding refunds by default: the ticket and its
/// dependents on this order, within what the order can still refund.
/// </summary>
internal sealed record TicketAdminViewModel(
    TicketCheckoutOrder Order,
    TicketCheckoutTicket Ticket,
    TicketCheckoutTicket? Parent,
    IReadOnlyList<TicketCheckoutTicket> Dependents,
    TicketResaleOffer? LatestOffer,
    long SuggestedRefundMinor,
    long RefundableMinor);

internal sealed record CancelViewModel(Guid SalesEventId, string EventName, string BackendKey, CancellationProgress Progress);
