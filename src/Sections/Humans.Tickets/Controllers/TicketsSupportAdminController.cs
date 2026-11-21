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
/// The ticketing team's customer service pages over the checkout lane: lookup, refund list, resale queue, report, and
/// the ticket exceptions (rename, void, free companion). English only.
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
            await checkout.GetOffersForSalesEventAsync(id, ct), tickets, await checkout.GetRefundsForSalesEventAsync(id, ct),
            clock.GetCurrentInstant());
        var sold = tickets.Count(t => t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered);
        return View("~/Views/Tickets/Admin/Sales/Resale.cshtml", new ResaleViewModel(
            id, await EventNameAsync(salesEvent, ct), salesEvent, sold, rows));
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
        return View("~/Views/Tickets/Admin/Sales/Ticket.cshtml", new TicketAdminViewModel(
            detail.Order, ticket,
            ticket.ParentTicketId is { } parentId ? eventTickets.FirstOrDefault(t => t.Id == parentId) : null,
            eventTickets.Where(t => t.ParentTicketId == ticket.Id).ToList(),
            offer));
    }

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/Rename")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Rename(Guid orderId, Guid ticketId, string? holderName, string? holderEmail, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Ticket renamed.", (actor, c) =>
            checkout.RenameTicketAsync(ticketId, holderName ?? string.Empty, holderEmail ?? string.Empty, reason ?? string.Empty, actor, c), ct);

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/Void")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Void(Guid orderId, Guid ticketId, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Ticket voided. Refund from the order page if money is owed.", (actor, c) =>
            checkout.VoidTicketAsync(ticketId, reason ?? string.Empty, actor, c), ct);

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/Companion")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Companion(Guid orderId, Guid ticketId, string? holderName, string? holderEmail, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Companion ticket issued.", (actor, c) =>
            checkout.IssueCompanionAsync(ticketId, holderName ?? string.Empty, holderEmail, reason ?? string.Empty, actor, c), ct);

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

/// <summary><paramref name="Sold"/>: live tickets, which open resale at the event's threshold.</summary>
internal sealed record ResaleViewModel(
    Guid SalesEventId, string EventName, TicketSalesEvent SalesEvent, int Sold, IReadOnlyList<ResaleQueueRow> Rows)
{
    public TicketSalesMode Mode => SalesEvent.Mode;
    public int? OpensAtSold => SalesEvent.ResaleOpensAtSold;
    public bool ResaleOpen => SalesEvent.ResaleOpenAt(Sold);
    public bool SwitchedOff => SalesEvent.EffectiveSwitches.Resale == ResaleScheme.Off;
}

internal sealed record ReportViewModel(Guid SalesEventId, string EventName, int Capacity, SalesReport Report);

internal sealed record TicketAdminViewModel(
    TicketCheckoutOrder Order,
    TicketCheckoutTicket Ticket,
    TicketCheckoutTicket? Parent,
    IReadOnlyList<TicketCheckoutTicket> Dependents,
    TicketResaleOffer? LatestOffer);
