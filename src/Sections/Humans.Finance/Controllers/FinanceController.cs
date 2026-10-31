using System.Globalization;
using System.Text;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Users.Contracts;

namespace Humans.Finance.Controllers;

/// <summary>Budget's <c>BudgetAdminController</c> shares the <c>Finance</c> route prefix.</summary>
[Authorize(Policy = FinancePolicies.Read)]
[Route("Finance")]
internal sealed class FinanceController(
    IUserService userService,
    IHoldedFinanceManagement holdedFinance,
    IHoldedFinanceAdminService holdedConnector,
    IExpenseReportService expenseReports,
    CashFlowService cashFlow,
    CreditorMatchService creditorMatches,
    ILogger<FinanceController> logger) : HumansControllerBase(userService)
{
    [HttpGet("CashFlow")]
    public async Task<IActionResult> CashFlow(string period = "monthly")
    {
        try
        {
            var model = await cashFlow.GetActiveYearCashFlowAsync(period, HttpContext.RequestAborted);
            if (model is not null) return View(model);
            SetInfo("No active budget year.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading cash flow view");
            SetError("Failed to load cash flow data.");
        }
        return RedirectToAction("Index", "BudgetAdmin");
    }

    /// <summary>Cache reads only.</summary>
    [HttpGet("Holded")]
    public async Task<IActionResult> Holded(CancellationToken ct)
        => View(await holdedConnector.GetConnectorOverviewAsync(ct));

    [HttpGet("HoldedAccounts")]
    public async Task<IActionResult> HoldedAccounts(int blockStart = 62900100)
    {
        var plan = await holdedConnector.GetProvisioningPlanAsync(blockStart, HttpContext.RequestAborted);
        ViewBag.BlockStart = blockStart;
        return View(plan);
    }

    [HttpPost("HoldedAccounts/Provision")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionHoldedAccounts([BindRequired] int blockStart, [BindRequired] bool addAll)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed Holded provisioning form for actor {ActorUserId}", actorUserId);
            return BadRequest(ModelState);
        }
        try
        {
            var n = await holdedConnector.ProvisionAsync(blockStart, addAll, actorUserId);
            SetSuccess($"Provisioned {n} Holded account(s).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Holded provisioning failed");
            SetError("Provisioning failed.");
        }
        return RedirectToAction(nameof(HoldedAccounts), new { blockStart });
    }

    [HttpGet("HoldedUnmatched")]
    public async Task<IActionResult> HoldedUnmatched()
        => View((await holdedFinance.GetUnmatchedAsync(HttpContext.RequestAborted)).OrderByDescending(d => d.Date).ToList());

    [HttpGet("Creditors")]
    public async Task<IActionResult> Creditors(string? sort, string? dir)
    {
        if (GetCurrentUserId() is not { } viewerUserId) return Challenge();
        var (rows, unresolved) = await holdedFinance.ListCreditorAccountsAsync(HttpContext.RequestAborted);
        var matches = await creditorMatches.GetForMemberAccountsAsync(rows, viewerUserId, HttpContext.RequestAborted);

        var names = new Dictionary<Guid, string>();
        var boundIds = rows.SelectMany(r => r.Bindings).Select(b => b.UserId)
            .Concat(unresolved.Select(b => b.UserId))
            .Distinct().ToList();
        if (boundIds.Count > 0)
            foreach (var kv in await UserService.GetUserInfosAsync(boundIds, HttpContext.RequestAborted))
                names[kv.Key] = kv.Value.BurnerName;

        // Display only: shown inverted, so positive is owed to the member. The contract row keeps Holded's
        // Σdebit − Σcredit.
        var vms = rows
            .Select(r => new CreditorAccountRowVm(
                r.SupplierAccountNum, r.Name, r.Balance is { } bal ? -bal : null,
                r.Bindings.Select(b => new CreditorBindingVm(
                    b.UserId,
                    names.TryGetValue(b.UserId, out var nm) ? nm : b.UserId.ToString(),
                    b.Source.ToString())).ToList(),
                r.IbanMasked,
                r.OpenDocumentCount,
                r.PendingPayoutFileName,
                r.HasDuplicateDocuments,
                matches.GetValueOrDefault(r.SupplierAccountNum)))
            .ToList();

        var sortBy = sort is "name" or "balance" or "member" or "opendocs" ? sort : "account";
        var sortDir = string.Equals(dir, "desc", StringComparison.Ordinal) ? "desc" : "asc";
        var accounts = SortCreditorRows(vms, sortBy, sortDir);

        var unresolvedVm = unresolved
            .Select(b => new CreditorBindingVm(
                b.UserId,
                names.TryGetValue(b.UserId, out var nm) ? nm : b.UserId.ToString(),
                b.Source.ToString()))
            .ToList();

        var recon = await holdedFinance.GetExpenseReconAsync(null, HttpContext.RequestAborted);
        return View(new CreditorsPageVm(
            accounts, unresolvedVm, sortBy, sortDir, holdedConnector.GetSepaPayoutSettings(),
            recon.IsAvailable ? recon.Totals.Problems : null, recon.Totals.Warnings));
    }

    /// <summary>Local mirrors only; fixes stay on the pages it links to.</summary>
    [HttpGet("ExpenseRecon")]
    public async Task<IActionResult> ExpenseRecon(int? year, CancellationToken ct)
        => View(await holdedFinance.GetExpenseReconAsync(year, ct));

    /// <summary>Streams a pain.001.001.09 file. All-or-nothing: one bad amount refuses the batch, so the treasurer
    /// never uploads half of one.</summary>
    [HttpPost("Sepa/Generate")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateSepa(
        int[] selected, Dictionary<int, string> amounts, string maxPerTransfer, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed SEPA payout selection for actor {ActorUserId}", actorUserId);
            return BadRequest(ModelState);
        }

        // Parsed invariantly like the amounts below. The posted value is authoritative; Sepa:MaxPayoutPerTransfer is
        // only the prefill.
        if (!decimal.TryParse(maxPerTransfer, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap)
            || cap <= 0m)
        {
            SetError("The per-transfer cap must be a positive amount.");
            return RedirectToAction(nameof(Creditors));
        }

        // A checkbox posts only when ticked, so `selected` is the batch, keyed like the amount boxes. Parsed
        // invariantly, not model-bound: `<input type="number">` posts "12.34", which a comma-decimal culture reads as
        // 1234. A missing or unparseable amount stays 0 and is refused.
        var picked = selected
            .Select(num => new SepaPayoutSelection(
                num,
                amounts.TryGetValue(num, out var raw)
                && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                    ? amount
                    : 0m))
            .ToList();

        var result = await holdedConnector.GenerateSepaPayoutAsync(picked, cap, actorUserId, ct);
        if (!result.Succeeded)
        {
            SetError(result.ErrorMessage!);
            return RedirectToAction(nameof(Creditors));
        }

        return File(Encoding.UTF8.GetBytes(result.Xml!), "application/xml", result.FileName!);
    }

    /// <summary>Newest file first.</summary>
    [HttpGet("Sepa")]
    public async Task<IActionResult> Sepa(
        CancellationToken ct, int days = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        var (rows, unavailable, unmatched, bankFeedError) = await holdedConnector.GetSepaPayoutsAsync(days, ct);

        var names = new Dictionary<Guid, string>();
        var ids = rows.SelectMany(r => new[] { r.UserId, r.GeneratedByUserId })
            .Concat(rows.Where(r => r.BookedByUserId is not null).Select(r => r.BookedByUserId!.Value))
            .Concat(rows.Where(r => r.WithdrawnByUserId is not null).Select(r => r.WithdrawnByUserId!.Value))
            .Distinct().ToList();
        if (ids.Count > 0)
            foreach (var kv in await UserService.GetUserInfosAsync(ids, ct))
                names[kv.Key] = kv.Value.BurnerName;

        string Name(Guid? id) =>
            id is { } g && names.TryGetValue(g, out var nm) ? nm : id?.ToString() ?? "—";

        var files = rows
            .GroupBy(r => r.FileId)
            .Select(g => new SepaPayoutFileVm(
                g.Key,
                g.First().FileName,
                g.First().GeneratedAt,
                Name(g.First().GeneratedByUserId),
                g.OrderBy(r => r.SupplierAccountNum)
                 .Select(r => new SepaTransferVm(r, Name(r.UserId), r.BookedByUserId is null ? null : Name(r.BookedByUserId),
                     r.WithdrawnByUserId is null ? null : Name(r.WithdrawnByUserId)))
                 .ToList(),
                g.First().BatchLine))
            .OrderByDescending(f => f.GeneratedAt)
            .ToList();

        return View(new SepaPayoutsPageVm(files, unavailable, unmatched, bankFeedError, days));
    }

    /// <summary>No <c>CancellationToken</c> reaches the service: a booking that has posted a payment must finish
    /// even if the admin closes the tab.</summary>
    [HttpPost("Sepa/Book")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookSepaTransfer(
        Guid transferId, string bankMovementId,
        int days = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();

        var result = await holdedConnector.BookSepaTransferAsync(transferId, bankMovementId, actorUserId, days);
        if (result.Succeeded) SetSuccess(result.Message); else SetError(result.Message);

        return RedirectToAction(nameof(Sepa), new { days });
    }

    /// <summary>For a transfer the bank cancelled by hand before execution.</summary>
    [HttpPost("Sepa/Withdraw")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawSepaTransfer(
        Guid transferId, int days = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();

        var result = await holdedConnector.WithdrawSepaTransferAsync(transferId, actorUserId, HttpContext.RequestAborted);
        if (result.Succeeded) SetSuccess(result.Message); else SetError(result.Message);

        return RedirectToAction(nameof(Sepa), new { days });
    }

    /// <summary>Dry run from live Holded reads; nothing is written.</summary>
    [HttpGet("HoldedDuplicates")]
    public async Task<IActionResult> HoldedDuplicates(CancellationToken ct)
    {
        var plan = await holdedConnector.GetHoldedDuplicatesAsync(ct);
        ViewBag.MemberNames = await MemberNamesAsync(plan.Members.Select(m => m.UserId), ct);
        return View(plan);
    }

    /// <summary>No <c>CancellationToken</c>: a run that has deleted one document finishes the rest.</summary>
    [HttpPost("HoldedDuplicates/Apply")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyHoldedDuplicates()
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();

        var result = await holdedConnector.RemoveHoldedDuplicatesAsync(actorUserId);
        ViewBag.MemberNames = await MemberNamesAsync(result.Members.Select(m => m.UserId), HttpContext.RequestAborted);
        return View("HoldedDuplicatesApplied", result);
    }

    private async Task<Dictionary<Guid, string>> MemberNamesAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        var names = new Dictionary<Guid, string>();
        if (ids.Count > 0)
            foreach (var kv in await UserService.GetUserInfosAsync(ids, ct))
                names[kv.Key] = kv.Value.BurnerName;
        return names;
    }

    /// <summary>Books a whole file against the one Sabadell line the bank debited it as. No
    /// <c>CancellationToken</c>, as for a single booking.</summary>
    [HttpPost("Sepa/BookFile")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookSepaFile(
        Guid fileId, string bankMovementId,
        int days = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();

        var result = await holdedConnector.BookSepaFileAsync(fileId, bankMovementId, actorUserId, days);
        if (result.Succeeded) SetSuccess(result.Message); else SetError(result.Message);

        return RedirectToAction(nameof(Sepa), new { days });
    }

    /// <summary>For a file whose every unbooked transfer is matched to its own Sabadell line. No
    /// <c>CancellationToken</c>, as for a single booking.</summary>
    [HttpPost("Sepa/BookMatches")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookSepaFileMatches(
        Guid fileId, int days = IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();

        var result = await holdedConnector.BookSepaFileMatchesAsync(fileId, actorUserId, days);
        if (result.Succeeded) SetSuccess(result.Message); else SetError(result.Message);

        return RedirectToAction(nameof(Sepa), new { days });
    }

    /// <summary>Ties resolve on the account number so the order is stable across reloads.</summary>
    private static List<CreditorAccountRowVm> SortCreditorRows(
        IReadOnlyList<CreditorAccountRowVm> rows, string sortBy, string sortDir)
    {
        Func<CreditorAccountRowVm, IComparable> key = sortBy switch
        {
            "name" => r => r.Name.ToUpperInvariant(),
            "balance" => r => r.Balance ?? 0m,
            "opendocs" => r => r.OpenDocs ?? -1,
            "member" => r => r.MemberSortKey,
            _ => r => r.SupplierAccountNum,
        };

        var ordered = string.Equals(sortDir, "desc", StringComparison.Ordinal)
            ? rows.OrderByDescending(key)
            : rows.OrderBy(key);
        return ordered.ThenBy(r => r.SupplierAccountNum).ToList();
    }

    [HttpGet("Creditors/{accountNum:int}")]
    public async Task<IActionResult> CreditorStatement(int accountNum)
    {
        if (GetCurrentUserId() is not { } viewerUserId) return Challenge();
        var ledger = await holdedFinance.GetCreditorLedgerAsync(accountNum, HttpContext.RequestAborted);
        if (ledger is null) return NotFound();

        // The finance-admin review queue is every submitted report; the ones booked to this account are the other
        // side of the ledger.
        var reports = (await expenseReports.GetReviewQueueAsync(viewerUserId, isFinanceAdmin: true, HttpContext.RequestAborted))
            .Where(r => r.HoldedSupplierAccountNum == accountNum)
            .OrderByDescending(r => r.ApprovedAt ?? r.SubmittedAt ?? r.CreatedAt)
            .ToList();

        // The running balance is the service's; the page shows newest activity first.
        var memberName = string.IsNullOrWhiteSpace(ledger.Contact?.Name) ? "member" : ledger.Contact.Name;
        var lines = ledger.Lines
            .Select(l => new CreditorStatementLineVm(l, l.RunningBalance, StatementDescription(l, memberName)))
            .OrderByDescending(r => r.Line.Date)
            .ThenByDescending(r => r.Line.EntryNumber)
            .ThenBy(r => r.Line.Line)
            .ToList();
        var match = await creditorMatches.GetAsync(accountNum, viewerUserId, HttpContext.RequestAborted);
        return View(new CreditorStatementVm(ledger, lines, reports, match));
    }

    // Holded labels a payment with the purchase it settles, which reads like a second purchase.
    private static string? StatementDescription(CreditorLedgerLine line, string memberName)
    {
        if (!string.Equals(line.Type, "payment", StringComparison.OrdinalIgnoreCase))
            return line.Description;
        return line.Debit > 0
            ? $"Payment: NCA → {memberName}"
            : $"Payment: {memberName} → NCA";
    }

    [HttpPost("Creditors/Bind")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BindCreditor(Guid userId, int supplierAccountNum, string? returnUrl)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();
        try
        {
            var result = await holdedConnector.SetCreditorContactAsync(userId, supplierAccountNum, actorUserId);
            if (result.Succeeded)
                SetSuccess($"Bound member to creditor account {supplierAccountNum}.");
            else
                SetError(result.ErrorMessage ?? $"Could not bind account {supplierAccountNum}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to bind user {UserId} to creditor account {AccountNum}", userId, supplierAccountNum);
            SetError("Failed to bind creditor account.");
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction(nameof(Creditors));
    }

    [HttpPost("Creditors/Unbind")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnbindCreditor(Guid userId, string? returnUrl)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Challenge();
        try
        {
            if (await holdedConnector.ClearCreditorContactAsync(userId, actorUserId))
                SetSuccess("Cleared the member's creditor account binding.");
            else
                SetError("That member has no creditor account binding to clear.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear the creditor binding for user {UserId}", userId);
            SetError("Failed to clear the creditor account binding.");
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction(nameof(Creditors));
    }

    [HttpPost("HoldedSync/Run")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunHoldedSync()
    {
        try
        {
            var r = await holdedFinance.SyncAsync();
            SetSuccess($"Synced {r.DocCount} docs ({r.Matched} matched, {r.Unmatched} unmatched).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Holded sync failed");
            SetError("Sync failed.");
        }
        return RedirectToAction(nameof(HoldedUnmatched));
    }
}
