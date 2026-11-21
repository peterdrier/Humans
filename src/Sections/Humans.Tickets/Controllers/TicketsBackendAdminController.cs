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
/// One checkout backend's status page, the target of that backend's admin sidebar row: its settings, the events
/// selling through it, and the orders and refunds it holds. English only.
/// </summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets/Admin/Backends/{key}")]
internal sealed class TicketsBackendAdminController(
    ITicketCheckoutService checkout,
    ISettingsService settings,
    IClock clock,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string key, CancellationToken ct) =>
        await StatusAsync(key, null, ct);

    /// <summary>Changes nothing; Manage only because it calls the vendor with Humans' credentials.</summary>
    [HttpPost("Check")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Check(string key, string? backendEventRef, CancellationToken ct)
    {
        var findings = await checkout.CheckBackendAsync(key, backendEventRef, ct);
        if (findings is null)
            return NotFound();
        return await StatusAsync(key, new BackendCheckResult(key, backendEventRef, findings), ct);
    }

    private async Task<IActionResult> StatusAsync(string key, BackendCheckResult? check, CancellationToken ct)
    {
        var backend = checkout.Backends.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));
        if (backend is null)
            return NotFound();

        var salesEvents = await checkout.GetSalesEventsAsync(ct);
        var orders = new List<TicketCheckoutOrder>();
        var refunds = new List<TicketCheckoutRefund>();
        foreach (var salesEvent in salesEvents)
        {
            var eventOrders = (await checkout.GetOrdersForSalesEventAsync(salesEvent.Id, ct))
                .Where(o => string.Equals(o.BackendKey, key, StringComparison.Ordinal)).ToList();
            if (eventOrders.Count == 0)
                continue;
            orders.AddRange(eventOrders);
            var ids = eventOrders.Select(o => o.Id).ToHashSet();
            refunds.AddRange((await checkout.GetRefundsForSalesEventAsync(salesEvent.Id, ct)).Where(r => ids.Contains(r.OrderId)));
        }

        return View("~/Views/Tickets/Admin/Backends/Index.cshtml", new BackendStatusViewModel(
            new BackendsViewModel([backend], salesEvents, await settings.GetAllEventSettingsAsync(ct), check),
            orders.OrderByDescending(o => o.CreatedAt).ToList(),
            refunds,
            clock.GetCurrentInstant()));
    }
}

/// <summary>
/// <see cref="Stuck"/> are orders the backend has not settled when it should have: never opened at the backend, or
/// still open past their expiry, which means neither a webhook nor the five-minute reconcile has landed.
/// </summary>
internal sealed record BackendStatusViewModel(
    BackendsViewModel Backend,
    IReadOnlyList<TicketCheckoutOrder> Orders,
    IReadOnlyList<TicketCheckoutRefund> Refunds,
    Instant Now)
{
    private static readonly Duration Grace = Duration.FromMinutes(15);

    public CheckoutBackendInfo Info => Backend.Backends[0];

    public IReadOnlyList<TicketCheckoutOrder> Stuck => Orders.Where(o =>
            (o.State == TicketCheckoutOrderState.Created && o.BackendOrderId is null && o.CreatedAt + Grace < Now)
            || (o.State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending && o.ExpiresAt + Grace < Now))
        .ToList();

    public IReadOnlyList<TicketCheckoutRefund> OpenRefunds =>
        Refunds.Where(r => r.State is not RefundState.Succeeded).OrderBy(r => r.State).ToList();
}
