using Humans.Base.Controllers;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.Controllers;

/// <summary>The ticketing team's customer service pages over the checkout lane. English only.</summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets/Admin/Sales")]
internal sealed class TicketsSupportAdminController(
    ITicketSupportService support,
    ITicketCheckoutService checkout,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("Lookup")]
    public async Task<IActionResult> Lookup(string? q, CancellationToken ct)
    {
        var hits = string.IsNullOrWhiteSpace(q) ? [] : await support.SearchOrdersAsync(q, ct);
        return View("~/Views/Tickets/Admin/Sales/Lookup.cshtml", new LookupViewModel(q?.Trim() ?? string.Empty, hits));
    }

    [HttpGet("{id:guid}/Refunds")]
    public async Task<IActionResult> Refunds(Guid id, RefundState? state, CancellationToken ct)
    {
        var salesEvent = await checkout.GetSalesEventAsync(id, ct);
        var rows = await support.GetRefundsAsync(id, state, ct);
        if (salesEvent is null || rows is null)
            return NotFound();
        return View("~/Views/Tickets/Admin/Sales/Refunds.cshtml", new RefundsViewModel(salesEvent.Id, state, rows));
    }

    [HttpGet("{id:guid}/Report")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        var report = await support.GetSalesReportAsync(id, ct);
        if (report is null)
            return NotFound();
        return View("~/Views/Tickets/Admin/Sales/Report.cshtml", new ReportViewModel(id, report));
    }
}

internal sealed record LookupViewModel(string Query, IReadOnlyList<OrderSearchHit> Hits);

internal sealed record RefundsViewModel(Guid SalesEventId, RefundState? State, IReadOnlyList<RefundListRow> Rows);

internal sealed record ReportViewModel(Guid SalesEventId, SalesReport Report);
