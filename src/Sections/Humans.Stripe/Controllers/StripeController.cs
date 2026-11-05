using Humans.Base.Controllers;
using Humans.Finance.Contracts;
using Humans.Stripe.Models;
using Humans.Stripe.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Humans.Stripe.Controllers;

[Authorize(Policy = FinancePolicies.Read)]
[Route("Stripe")]
internal sealed class StripeController(
    IUserService userService,
    IStripeMirrorAdminService stripe,
    ILogger<StripeController> logger) : HumansControllerBase(userService)
{
    /// <summary>The list page renders at most this many rows, newest first; the count above it says when more matched.</summary>
    internal const int TransactionDisplayCap = 1000;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await stripe.GetSummaryAsync(ct));

    [HttpPost("SyncNow")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        try
        {
            switch (await stripe.SyncAsync(ct))
            {
                case StripeSyncOutcome.Synced: SetSuccess("Stripe mirror refreshed."); break;
                case StripeSyncOutcome.SomeFailed: SetError("Some accounts failed to sync; the error is on their card."); break;
                case StripeSyncOutcome.Busy: SetInfo("A sweep was already running, so this one was skipped."); break;
                default: SetError("No Stripe mirror key is configured."); break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe sweep failed");
            SetError("Sync failed.");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Transactions")]
    public async Task<IActionResult> Transactions(
        string? account, string? type, string? q, string? payout, CancellationToken ct = default)
    {
        var page = await stripe.GetTransactionsPageAsync(new StripeTransactionsFilter(account, type, q, payout), ct);
        ViewData["MatchCount"] = page.Rows.Count;
        return View(page with
        {
            Rows = page.Rows.OrderByDescending(r => r.Created).ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(TransactionDisplayCap).ToList(),
            Types = page.Types.Order(StringComparer.Ordinal).ToList(),
        });
    }

    [HttpGet("Transactions/{id}")]
    public async Task<IActionResult> Transaction(string id, CancellationToken ct)
    {
        var view = await stripe.GetTransactionViewAsync(id, ct);
        return view is null ? NotFound() : View(view);
    }

    [HttpGet("Payouts")]
    public async Task<IActionResult> Payouts(string? account, CancellationToken ct = default)
    {
        var page = await stripe.GetPayoutsPageAsync(account, ct);
        return View(page with { Rows = page.Rows.OrderByDescending(r => r.Created).ToList() });
    }

    [HttpGet("Reserves")]
    public async Task<IActionResult> Reserves(CancellationToken ct) => View(await stripe.GetReservesPageAsync(ct));
}
