using Humans.Base.Controllers;
using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Text;

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
    IOrgTimeZone orgTimeZone,
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
        if (await checkout.GetBackendLedgerAsync(key, ct) is not { } ledger)
            return NotFound();

        ledger = ledger with
        {
            Orders = ledger.Orders.OrderByDescending(order => order.CreatedAt).ToList(),
            Stuck = ledger.Stuck.OrderByDescending(order => order.CreatedAt).ToList(),
            UnsettledRefunds = ledger.UnsettledRefunds.OrderBy(refund => refund.CreatedAt).ToList(),
            FailedRefunds = ledger.FailedRefunds.OrderBy(refund => refund.CreatedAt).ToList(),
        };
        return View("~/Views/Tickets/Admin/Backends/Index.cshtml", new BackendStatusViewModel(
            new BackendsViewModel([ledger.Backend], await checkout.GetSalesEventsAsync(ct), await settings.GetAllEventSettingsAsync(ct), check),
            ledger,
            ledger.Orders.Count == 0 ? null : LocalDatePattern.Iso.Format(ledger.Orders.Min(order => order.CreatedAt).InZone(orgTimeZone.Zone).Date)));
    }
}

/// <summary><c>ReconFrom</c> is the oldest order's day in the organisation's zone, so the reconciliation link covers every order.</summary>
internal sealed record BackendStatusViewModel(BackendsViewModel Backend, BackendLedger Ledger, string? ReconFrom)
{
    public CheckoutBackendInfo Info => Backend.Backends[0];
}
