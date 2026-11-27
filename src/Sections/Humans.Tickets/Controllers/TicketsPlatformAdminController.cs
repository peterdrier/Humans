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
/// One ticketing platform's status page, the target of that platform's admin sidebar row: its settings, the events
/// selling through it, and the orders and refunds it holds. English only.
/// </summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets/Admin/Platforms/{key}")]
internal sealed class TicketsPlatformAdminController(
    ITicketCheckoutService checkout,
    ISettingsService settings,
    IOrgTimeZone orgTimeZone,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string key, CancellationToken ct) =>
        await StatusAsync(key, null, ct);

    /// <summary>Changes nothing; Manage only because it calls the ticketing platform with Humans' credentials.</summary>
    [HttpPost("Check")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Check(string key, string? platformEventRef, CancellationToken ct)
    {
        var findings = await checkout.CheckPlatformAsync(key, platformEventRef, ct);
        if (findings is null)
            return NotFound();
        return await StatusAsync(key, new PlatformCheckResult(key, platformEventRef, findings), ct);
    }

    private async Task<IActionResult> StatusAsync(string key, PlatformCheckResult? check, CancellationToken ct)
    {
        if (await checkout.GetPlatformLedgerAsync(key, ct) is not { } ledger)
            return NotFound();

        ledger = ledger with
        {
            Orders = ledger.Orders.OrderByDescending(order => order.CreatedAt).ToList(),
            Stuck = ledger.Stuck.OrderByDescending(order => order.CreatedAt).ToList(),
            UnsettledRefunds = ledger.UnsettledRefunds.OrderBy(refund => refund.CreatedAt).ToList(),
            FailedRefunds = ledger.FailedRefunds.OrderBy(refund => refund.CreatedAt).ToList(),
        };
        return View("~/Views/Tickets/Admin/Platforms/Index.cshtml", new PlatformStatusViewModel(
            new PlatformsViewModel([ledger.Platform], await checkout.ListTicketingSettingsAsync(ct), await settings.GetAllEventSettingsAsync(ct), check),
            ledger,
            ledger.Orders.Count == 0 ? null : LocalDatePattern.Iso.Format(ledger.Orders.Min(order => order.CreatedAt).InZone(orgTimeZone.Zone).Date)));
    }
}

/// <summary><c>ReconFrom</c> is the oldest order's day in the organisation's zone, so the reconciliation link covers every order.</summary>
internal sealed record PlatformStatusViewModel(PlatformsViewModel Platform, PlatformLedger Ledger, string? ReconFrom)
{
    public PlatformInfo Info => Platform.Platforms[0];
}
