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
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var summary = await admin.GetSummaryAsync(ct);
        return View(summary with { SyncStates = summary.SyncStates.OrderBy(s => s.Kind, StringComparer.Ordinal).ToList() });
    }

    [HttpGet("Accounts")]
    public async Task<IActionResult> Accounts(
        bool archived = false, bool empty = false, bool mismatched = false, CancellationToken ct = default)
    {
        var page = await admin.GetAccountsPageAsync(new HoldedAccountsFilter(archived, empty, mismatched), ct);
        return View(page with
        {
            Groups = page.Groups.OrderBy(group => group.Digit)
                .Select(group => group with
                {
                    Accounts = group.Accounts.OrderBy(account => account.Number).ToList(),
                    Subgroups = group.Subgroups.OrderBy(sub => sub.Number)
                        .Select(sub => sub with { Accounts = sub.Accounts.OrderBy(account => account.Number).ToList() })
                        .ToList()
                })
                .ToList()
        });
    }

    [HttpGet("Purchases")]
    public async Task<IActionResult> Purchases(CancellationToken ct)
    {
        var mirror = await holded.GetPurchaseDocumentsAsync(ct);
        return View(mirror with
        {
            Docs = mirror.Docs.OrderByDescending(d => d.Date).ThenBy(d => d.DocNumber, StringComparer.Ordinal).ToList()
        });
    }

    [HttpGet("Calls")]
    public async Task<IActionResult> Calls(CancellationToken ct) =>
        View((await admin.GetCallsByMonthAsync(ct)).OrderBy(m => m.Year).ThenBy(m => m.Month).ToList());

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
        // Each sync on its own: a failed document pull must not skip the ledger sweep, or the reverse.
        var docs = await TrySyncAsync("purchase-document sync", () => holded.SyncPurchaseDocumentsAsync(ct), ct);
        var contacts = await TrySyncAsync("contact sync", () => holded.SyncContactsAsync(ct), ct);
        var ledger = await TrySyncAsync("ledger sweep", () => holded.SyncLedgerAsync(full: false, ct), ct);
        var failed = new[]
            {
                docs is null ? "purchase documents" : null, contacts is null ? "contacts" : null,
                ledger is null ? "ledger" : null,
            }
            .OfType<string>().ToList();
        if (failed.Count > 0)
            SetError($"Sync failed: {string.Join(" and ", failed)}.");
        else if (docs == true && contacts == true && ledger == true)
            SetSuccess("Synced the purchase documents and contacts, and swept the trailing ledger window.");
        else
            SetInfo("A sync was already running, so part of this one was skipped.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Null when the sync threw; caller cancellation still propagates.</summary>
    private async Task<bool?> TrySyncAsync(string name, Func<Task<bool>> sync, CancellationToken ct)
    {
        try
        {
            return await sync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Manual Holded {Sync} cancelled by the caller", name);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual Holded {Sync} failed", name);
            return null;
        }
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
