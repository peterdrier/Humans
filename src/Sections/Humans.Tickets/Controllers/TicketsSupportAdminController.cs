using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NodaTime;

namespace Humans.Tickets.Controllers;

/// <summary>
/// The ticketing team's customer service pages over ticket orders: lookup, refund list, resale queue, report, the
/// ticket exceptions (rename, void and refund, free companion), the audited overrides, and cancelling an event. English
/// only.
/// </summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets/Admin/Sales")]
internal sealed class TicketsSupportAdminController(
    ITicketCheckoutService checkout,
    ISettingsService settings,
    IClock clock,
    IUserService userService,
    ILogger<TicketsSupportAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("Lookup")]
    public async Task<IActionResult> Lookup(string? q, CancellationToken ct)
    {
        var query = q?.Trim() ?? string.Empty;
        var hits = new List<OrderSearchHit>();
        if (query.Length >= TicketSupportReports.MinimumQueryLength)
        {
            foreach (var ticketing in await checkout.ListTicketingSettingsAsync(ct))
            {
                var eventName = await EventNameAsync(ticketing, ct);
                var tickets = (await checkout.GetTicketsForSettingsAsync(ticketing.Id, ct))
                    .OrderBy(ticket => ticket.IssuedAt).ToLookup(t => t.OrderId);
                hits.AddRange((await checkout.GetOrdersForSettingsAsync(ticketing.Id, ct))
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
        var ticketing = await checkout.GetTicketingSettingsAsync(id, ct);
        if (ticketing is null)
            return NotFound();
        var rows = TicketSupportReports.RefundList(
            await checkout.GetOrdersForSettingsAsync(id, ct), await checkout.GetRefundsForSettingsAsync(id, ct), state)
            .OrderByDescending(row => row.Refund.CreatedAt).ToList();
        return View("~/Views/Tickets/Admin/Sales/Refunds.cshtml",
            new RefundsViewModel(id, await EventNameAsync(ticketing, ct), state, rows));
    }

    [HttpGet("{id:guid}/Resale")]
    public async Task<IActionResult> Resale(Guid id, CancellationToken ct)
    {
        var ticketing = await checkout.GetTicketingSettingsAsync(id, ct);
        if (ticketing is null)
            return NotFound();
        var tickets = await checkout.GetTicketsForSettingsAsync(id, ct);
        var rows = TicketSupportReports.ResaleQueue(
            await checkout.GetOffersForSettingsAsync(id, ct), tickets, await checkout.GetRefundsForSettingsAsync(id, ct))
            .OrderBy(row => row.Position is null).ThenBy(row => row.Position)
            .ThenByDescending(row => row.Offer.SoldAt ?? row.Offer.WithdrawnAt ?? row.Offer.OfferedAt).ToList();
        var reservedBy = (await checkout.GetOrdersForSettingsAsync(id, ct)).ToDictionary(o => o.Id);
        return View("~/Views/Tickets/Admin/Sales/Resale.cshtml", new ResaleViewModel(
            id, await EventNameAsync(ticketing, ct), ticketing, rows, reservedBy, clock.GetCurrentInstant()));
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
        var ticketing = await checkout.GetTicketingSettingsAsync(id, ct);
        var progress = await checkout.GetCancellationProgressAsync(id, ct);
        if (ticketing is null || progress is null)
            return NotFound();
        progress = progress with { RefundsSince = progress.RefundsSince.OrderBy(row => row.State).ToList() };
        return View("~/Views/Tickets/Admin/Sales/Cancel.cshtml", new CancelViewModel(
            id, await EventNameAsync(ticketing, ct), ticketing.PlatformKey, progress));
    }

    [HttpPost("{id:guid}/Cancel")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, string? reason, string? confirmName, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;
        var ticketing = await checkout.GetTicketingSettingsAsync(id, ct);
        if (ticketing is null)
            return NotFound();

        if (!string.Equals(confirmName?.Trim(), await EventNameAsync(ticketing, ct), StringComparison.Ordinal))
            SetError("Type the event's name exactly to confirm. Nothing was cancelled.");
        else if (await checkout.CancelEventAsync(id, reason ?? string.Empty, user.Id, ct) is { Succeeded: false } refused)
            SetError(refused.Refusal!);
        else
            SetSuccess("Event cancelled. Sales are closed; tickets are voided and orders refunded over the next runs, every five minutes.");
        return RedirectToAction(nameof(Cancel), new { id });
    }

    [HttpPost("{id:guid}/Resale/Open")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetResaleOpen(Guid id, [BindRequired] bool open, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed resale control for sale {SaleId} by {ActorUserId}", id, user.Id);
            return BadRequest(ModelState);
        }

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
        var ticketing = await checkout.GetTicketingSettingsAsync(id, ct);
        if (ticketing is null)
            return NotFound();
        var rows = (await checkout.GetCompanionRequestsAsync(id, ct))
            .OrderBy(row => row.Request.State != TicketCompanionRequestState.Pending)
            .ThenByDescending(row => row.Request.RequestedAt).ToList();
        return View("~/Views/Tickets/Admin/Sales/Companions.cshtml", new CompanionsViewModel(
            id, await EventNameAsync(ticketing, ct), rows));
    }

    [HttpPost("{id:guid}/Companions/{requestId:guid}")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideCompanion(Guid id, Guid requestId, [BindRequired] bool approve, string? note, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed companion decision for request {RequestId} by {ActorUserId}", requestId, user.Id);
            return BadRequest(ModelState);
        }

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
        var ticketing = await checkout.GetTicketingSettingsAsync(id, ct);
        if (ticketing is null)
            return NotFound();
        var evt = await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct);
        var report = TicketSupportReports.Sales(
            await checkout.GetOrdersForSettingsAsync(id, ct),
            await checkout.GetTicketsForSettingsAsync(id, ct),
            await checkout.GetRefundsForSettingsAsync(id, ct),
            await checkout.GetOffersForSettingsAsync(id, ct),
            evt?.TimeZone ?? DateTimeZone.Utc);
        report = report with
        {
            ByPrice = report.ByPrice.OrderByDescending(row => row.Count).ThenBy(row => row.Label, StringComparer.Ordinal).ToList(),
            DonationsByPurpose = report.DonationsByPurpose.OrderBy(row => row.Purpose, StringComparer.Ordinal).ToList(),
            RefundsByState = report.RefundsByState.OrderBy(row => row.State).ToList(),
            ResaleByState = report.ResaleByState.OrderBy(row => row.State).ToList(),
        };
        return View("~/Views/Tickets/Admin/Sales/Report.cshtml",
            new ReportViewModel(id, evt?.EventName ?? ticketing.EventSettingsId.ToString(), ticketing.Capacity, report));
    }

    [HttpGet("Order/{orderId:guid}/Ticket/{ticketId:guid}")]
    public async Task<IActionResult> Ticket(Guid orderId, Guid ticketId, CancellationToken ct)
    {
        var detail = await checkout.GetOrderDetailAsync(orderId, ct);
        var ticket = detail?.Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (detail is null || ticket is null)
            return NotFound();
        var eventTickets = await checkout.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct);
        var offer = (await checkout.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Where(o => o.TicketId == ticket.Id).MaxBy(o => o.OfferedAt);
        var dependents = eventTickets.Where(t => t.ParentTicketId == ticket.Id).OrderBy(t => t.IssuedAt).ToList();
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
    public async Task<IActionResult> Void(Guid orderId, Guid ticketId, string? reason, string? refund, [BindRequired] bool noRefund, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed ticket void for {TicketId} by {UserId}", ticketId, GetCurrentUserId());
            return BadRequest(ModelState);
        }
        var refundMinor = 0L;
        if (!noRefund && (!SalesEditForm.TryMinor(refund, out refundMinor) || refundMinor <= 0))
        {
            logger.LogWarning("Rejected void refund amount for ticket {TicketId} by {UserId}", ticketId, GetCurrentUserId());
            SetError("Enter the amount to refund, or choose 'No refund' to void without one. Nothing was voided.");
            return RedirectToAction(nameof(Ticket), new { orderId, ticketId });
        }
        // Voiding can refund at the platform; finish the refund and ticket changes after a disconnect.
        return await ActAsync(orderId, ticketId,
            noRefund ? "Ticket voided with no refund." : "Ticket voided and the refund sent; it shows on the order and the refund list.",
            (actor, _) => checkout.VoidTicketAsync(ticketId, reason ?? string.Empty, refundMinor, actor, CancellationToken.None), ct);
    }

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/Restore")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Restore(Guid orderId, Guid ticketId, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Override: ticket restored.", (actor, c) =>
            checkout.RestoreTicketAsync(ticketId, reason ?? string.Empty, actor, c), ct);

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/OverrideRename")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> OverrideRename(Guid orderId, Guid ticketId, string? holderName, string? holderEmail, string? reason, CancellationToken ct) =>
        ActAsync(orderId, ticketId, "Override: ticket renamed.", (actor, c) =>
            checkout.RenameTicketAsync(ticketId, holderName ?? string.Empty, holderEmail ?? string.Empty, reason ?? string.Empty,
                overrideRules: true, actor, c), ct);

    [HttpPost("Order/{orderId:guid}/Ticket/{ticketId:guid}/OverrideCompanion")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
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

    private async Task<string> EventNameAsync(TicketingSettings ticketing, CancellationToken ct) =>
        (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName ?? ticketing.EventSettingsId.ToString();
}

internal sealed record OrderSearchHit(TicketCheckoutOrder Order, IReadOnlyList<TicketCheckoutTicket> Tickets, string EventName);

internal sealed record LookupViewModel(string Query, IReadOnlyList<OrderSearchHit> Hits);

internal sealed record RefundsViewModel(Guid TicketingSettingsId, string EventName, RefundState? State, IReadOnlyList<RefundListRow> Rows);

/// <summary>The resale queue page. <c>Orders</c> holds the event's orders by id, to name the order holding a reserved ticket.</summary>
internal sealed record ResaleViewModel(
    Guid TicketingSettingsId, string EventName, TicketingSettings Ticketing, IReadOnlyList<ResaleQueueRow> Rows,
    IReadOnlyDictionary<Guid, TicketCheckoutOrder> Orders, Instant Now)
{
    /// <summary>
    /// A reserved offer whose order is no longer open or no longer holds its tickets (a payment on its way still does), which the
    /// reconcile job has not yet put right.
    /// </summary>
    public bool IsStuck(TicketResaleOffer offer) =>
        offer is { State: TicketResaleOfferState.Reserved, ReservedByOrderId: { } holdingId }
        && (Orders.GetValueOrDefault(holdingId) is not { IsOpen: true } holding || !holding.HoldsTickets(Now));

    public TicketSalesMode Mode => Ticketing.Mode;
    public bool ResaleSelling => Ticketing.ResaleSelling;
    public bool SwitchedOff => Ticketing.EffectiveSwitches.Resale == ResaleScheme.Off;
}

internal sealed record CompanionsViewModel(Guid TicketingSettingsId, string EventName, IReadOnlyList<CompanionRequestRow> Rows);

internal sealed record ReportViewModel(Guid TicketingSettingsId, string EventName, int Capacity, SalesReport Report);

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

internal sealed record CancelViewModel(Guid TicketingSettingsId, string EventName, string PlatformKey, CancellationProgress Progress);
