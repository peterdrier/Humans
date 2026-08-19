using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Holded.Models;
using Humans.Holded.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Holded.Controllers;

/// <summary>
/// <c>/Holded</c> — the mirror's own admin screen: what the API budget looks like, when each
/// sync last ran, which accounts reconcile, and where the department actuals stand. The two
/// sync buttons live here rather than on Finance's pages, because the mirror is this section's.
/// </summary>
[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
[Route("Holded")]
internal sealed class HoldedController(
    IUserServiceRead userService,
    IHoldedAdminService admin,
    IHoldedService holded,
    IHoldedFinanceService holdedFinance,
    ILogger<HoldedController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var overview = await admin.GetOverviewAsync(ct);
        var docSync = await holdedFinance.GetDocSyncInfoAsync(ct);

        return View(new HoldedOverviewVm(overview, docSync, docSync.CreditorBindingCount));
    }

    /// <summary>The general-ledger page for any account in the chart — a 629x department, a 572x
    /// bank, a 400x creditor. Native Holded sign throughout, so it always matches Holded's own view.</summary>
    [HttpGet("Accounts/{number:int}")]
    public async Task<IActionResult> Account(int number, CancellationToken ct)
    {
        var statement = await admin.GetAccountStatementAsync(number, ct);
        if (statement is null) return NotFound();
        return View(statement);
    }

    /// <summary>Every leg of one journal entry — the "+N more" landing page from the account
    /// statement's counterparty column, and the target of every Entry cell link.</summary>
    [HttpGet("Entries/{number:int}")]
    public async Task<IActionResult> Entry(int number, CancellationToken ct)
    {
        var entry = await admin.GetEntryAsync(number, ct);
        if (entry is null) return NotFound();
        return View(entry);
    }

    [HttpPost("SyncNow")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        try
        {
            var docs = await holdedFinance.SyncAsync(ct);
            var swept = await holded.SyncLedgerAsync(full: false, ct);
            if (swept)
                SetSuccess($"Synced {docs.DocCount} purchase doc(s) and swept the trailing ledger window.");
            else
                SetInfo($"Synced {docs.DocCount} purchase doc(s). A ledger sweep was already running, so this one was skipped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Holded sync failed");
            SetError("Sync failed.");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("FullSync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FullSync()
    {
        try
        {
            // The full-history sweep can outlive the HTTP request (proxy timeout, client
            // disconnect); the request token would cancel it mid-rebuild and record an error.
            if (await holded.SyncLedgerAsync(full: true, CancellationToken.None))
                SetSuccess("Full ledger resync complete — the mirror was rebuilt from inception.");
            else
                SetInfo("A ledger sweep was already running, so the full resync was skipped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Holded full ledger resync failed");
            SetError("Full resync failed.");
        }
        return RedirectToAction(nameof(Index));
    }
}
