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

[Authorize(Policy = FinancePolicies.Read)]
[Route("Holded")]
internal sealed class HoldedController(
    IUserService userService,
    IHoldedAdminService admin,
    IHoldedService holded,
    ILogger<HoldedController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(await admin.GetSummaryAsync(ct));

    [HttpGet("Accounts")]
    public async Task<IActionResult> Accounts(
        bool archived = false, bool empty = false, bool mismatched = false, CancellationToken ct = default) =>
        View(await admin.GetAccountsPageAsync(new HoldedAccountsFilter(archived, empty, mismatched), ct));

    [HttpGet("Calls")]
    public async Task<IActionResult> Calls(CancellationToken ct) =>
        View(await admin.GetCallsByMonthAsync(ct));

    /// <summary>Any account in the chart. Figures are in the association's point of view (+ money in, − money
    /// out), not Holded's raw debit/credit sign.</summary>
    [HttpGet("Accounts/{number:int}")]
    public async Task<IActionResult> Account(int number, CancellationToken ct)
    {
        var statement = await admin.GetAccountStatementAsync(number, ct);
        if (statement is null) return NotFound();
        return View(statement);
    }

    [HttpGet("Entries/{number:int}")]
    public async Task<IActionResult> Entry(int number, CancellationToken ct)
    {
        var entry = await admin.GetEntryViewAsync(number, ct);
        if (entry is null) return NotFound();
        return View(entry);
    }

    [HttpPost("SyncNow")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        try
        {
            if (await holded.SyncLedgerAsync(full: false, ct))
                SetSuccess("Swept the trailing ledger window.");
            else
                SetInfo("A ledger sweep was already running, so this one was skipped.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Manual Holded sync cancelled by the caller");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Holded ledger sweep failed");
            SetError("Sync failed.");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("FullSync")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FullSync()
    {
        try
        {
            // The full sweep can outlive the request; the request token would cancel it mid-rebuild.
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
