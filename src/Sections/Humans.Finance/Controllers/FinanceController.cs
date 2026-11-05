using System.Globalization;
using System.Text;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Finance.Controllers;

[Authorize(Policy = FinancePolicies.Read)]
[Route("Finance")]
internal sealed class FinanceController(
    IUserService userService,
    IFinanceService finance,
    IFinanceAdminService holdedConnector,
    IHoldedService holded,
    IExpenseReportService expenseReports,
    CashFlowService cashFlow,
    CreditorMatchService creditorMatches,
    FinanceOverviewService overview,
    ILogger<FinanceController> logger) : HumansControllerBase(userService)
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        if (GetCurrentUserId() is not { } viewerUserId) return Challenge();
        return View(await overview.GetAsync(viewerUserId, HttpContext.RequestAborted));
    }

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

    [HttpGet("HoldedAccounts")]
    public async Task<IActionResult> HoldedAccounts(int blockStart = 62900100)
    {
        var plan = await holdedConnector.GetProvisioningPlanAsync(blockStart, HttpContext.RequestAborted);
        ViewBag.BlockStart = blockStart;
        ViewBag.ManagedAccounts = (await holdedConnector.GetManagedAccountsAsync(HttpContext.RequestAborted))
            .OrderBy(m => m.AccountNumber).ToList();
        return View(plan);
    }

    [HttpPost("HoldedAccounts/Provision")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionHoldedAccounts([BindRequired] int blockStart, [BindRequired] bool addAll)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
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
        => View((await finance.GetUnmatchedAsync(HttpContext.RequestAborted)).OrderByDescending(d => d.Date).ToList());

    [HttpGet("Creditors")]
    public async Task<IActionResult> Creditors(string? sort, string? dir)
    {
        if (GetCurrentUserId() is not { } viewerUserId) return Challenge();
        var (rows, unresolved, unavailable) = await finance.ListCreditorAccountsAsync(HttpContext.RequestAborted);
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
                r.Bindings.OrderBy(b => b.CreatedAt).Select(b => new CreditorBindingVm(
                    b.UserId,
                    names.TryGetValue(b.UserId, out var nm) ? nm : b.UserId.ToString(),
                    b.Source.ToString())).ToList(),
                r.IbanMasked,
                r.OpenDocumentCount,
                r.PendingPayoutFileName,
                r.HasDuplicateDocuments,
                matches.GetValueOrDefault(r.SupplierAccountNum), r.NotPayableReason))
            .ToList();

        var sortBy = sort is "name" or "balance" or "member" or "opendocs" ? sort : "account";
        var sortDir = string.Equals(dir, "desc", StringComparison.Ordinal) ? "desc" : "asc";
        var accounts = SortCreditorRows(vms, sortBy, sortDir);

        var unresolvedVm = unresolved
            .OrderBy(b => b.CreatedAt)
            .Select(b => new CreditorBindingVm(
                b.UserId,
                names.TryGetValue(b.UserId, out var nm) ? nm : b.UserId.ToString(),
                b.Source.ToString()))
            .ToList();

        var recon = await finance.GetExpenseReconAsync(null, HttpContext.RequestAborted);
        return View(new CreditorsPageVm(
            accounts, unresolvedVm, sortBy, sortDir, holdedConnector.GetSepaPayoutSettings(),
            recon.IsAvailable ? recon.Totals.Problems : null, recon.Totals.Warnings, unavailable));
    }

    /// <summary>Local mirrors only; fixes stay on the pages it links to.</summary>
    [HttpGet("ExpenseRecon")]
    public async Task<IActionResult> ExpenseRecon(int? year, CancellationToken ct)
        => View(await finance.GetExpenseReconAsync(year, ct));

    /// <summary>The "are you sure" step: the same refusals as generation, nothing written. Posted, not linked, because
    /// the selection is a form.</summary>
    [HttpPost("Sepa/Review")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewSepa(
        int[] selected, Dictionary<int, string> amounts, string maxPerTransfer, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } viewerUserId) return Challenge();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed SEPA payout selection for actor {ActorUserId}", viewerUserId);
            return BadRequest(ModelState);
        }
        if (ParseSepaForm(selected, amounts, maxPerTransfer) is not (var picked, var cap))
            return RedirectToAction(nameof(Creditors));

        var preview = await holdedConnector.PreviewSepaPayoutAsync(picked, cap, ct);
        if (!preview.Succeeded)
        {
            SetError(preview.ErrorMessage!);
            return RedirectToAction(nameof(Creditors));
        }

        var paid = preview.Lines.Select(l => l.SupplierAccountNum).ToHashSet();
        var (rows, _, _) = await finance.ListCreditorAccountsAsync(ct);
        var matches = await creditorMatches.GetForMemberAccountsAsync(
            rows.Where(r => paid.Contains(r.SupplierAccountNum)), viewerUserId, ct);
        return View("SepaReview", new SepaReviewVm(preview.Lines, matches, cap));
    }

    /// <summary>Streams a pain.001.001.09 file. All-or-nothing: one bad amount refuses the batch, so the treasurer
    /// never uploads half of one.</summary>
    [HttpPost("Sepa/Generate")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateSepa(
        int[] selected, Dictionary<int, string> amounts, string maxPerTransfer, CancellationToken ct)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed SEPA payout selection for actor {ActorUserId}", actorUserId);
            return BadRequest(ModelState);
        }
        if (ParseSepaForm(selected, amounts, maxPerTransfer) is not (var picked, var cap))
            return RedirectToAction(nameof(Creditors));

        var result = await holdedConnector.GenerateSepaPayoutAsync(picked, cap, actorUserId, ct);
        if (!result.Succeeded)
        {
            SetError(result.ErrorMessage!);
            return RedirectToAction(nameof(Creditors));
        }

        return File(Encoding.UTF8.GetBytes(result.Xml!), "application/xml", result.FileName!);
    }

    /// <summary>Null, with the error set, when the cap is unusable.</summary>
    private (List<SepaPayoutSelection> Picked, decimal Cap)? ParseSepaForm(
        int[] selected, Dictionary<int, string> amounts, string maxPerTransfer)
    {
        // Parsed invariantly like the amounts below. The posted value is authoritative; Sepa:MaxPayoutPerTransfer is
        // only the prefill. Grouping is forbidden so a comma decimal cannot silently become a larger amount.
        if (!decimal.TryParse(maxPerTransfer, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var cap)
            || cap <= 0m)
        {
            SetError("The per-transfer cap must be a positive amount.");
            return null;
        }

        // A checkbox posts only when ticked, so `selected` is the batch, keyed like the amount boxes. Parsed
        // invariantly, not model-bound: `<input type="number">` posts "12.34", which a comma-decimal culture reads as
        // 1234. A missing or unparseable amount stays 0 and is refused.
        var picked = selected
            .Select(num => new SepaPayoutSelection(
                num,
                amounts.TryGetValue(num, out var raw)
                && decimal.TryParse(raw, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var amount)
                    ? amount
                    : 0m))
            .ToList();
        return (picked, cap);
    }

    /// <summary>Newest file first.</summary>
    [HttpGet("Sepa")]
    public async Task<IActionResult> Sepa(
        CancellationToken ct, int days = IFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        var (rows, unavailable, unmatched, bankFeedError) = await holdedConnector.GetSepaPayoutsAsync(days, ct);

        var names = new Dictionary<Guid, string>();
        var ids = rows.Select(r => r.UserId).Distinct().ToList();
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
                g.First().GeneratedByUserId,
                g.OrderBy(r => r.SupplierAccountNum)
                 .Select(r => new SepaTransferVm(r, Name(r.UserId)))
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
        [BindRequired] Guid transferId, [BindRequired] string bankMovementId,
        int days = IFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
        if (!ModelState.IsValid) return MalformedMutation(actorUserId);

        var result = await holdedConnector.BookSepaTransferAsync(transferId, bankMovementId, actorUserId, days);
        if (result.Succeeded) SetSuccess(result.Message); else SetError(result.Message);

        return RedirectToAction(nameof(Sepa), new { days });
    }

    /// <summary>For a transfer the bank cancelled by hand before execution.</summary>
    [HttpPost("Sepa/Withdraw")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawSepaTransfer(
        [BindRequired] Guid transferId, int days = IFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
        if (!ModelState.IsValid) return MalformedMutation(actorUserId);

        var result = await holdedConnector.WithdrawSepaTransferAsync(transferId, actorUserId, HttpContext.RequestAborted);
        if (result.Succeeded) SetSuccess(result.Message); else SetError(result.Message);

        return RedirectToAction(nameof(Sepa), new { days });
    }

    /// <summary>For a file the bank rejected whole.</summary>
    [HttpPost("Sepa/CancelFile")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSepaFile(
        [BindRequired] Guid fileId, int days = IFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
        if (!ModelState.IsValid) return MalformedMutation(actorUserId);

        var result = await holdedConnector.CancelSepaFileAsync(fileId, actorUserId, HttpContext.RequestAborted);
        if (result.Succeeded) SetSuccess(result.Message); else SetError(result.Message);

        return RedirectToAction(nameof(Sepa), new { days });
    }

    /// <summary>Dry run from live Holded reads; nothing is written.</summary>
    [HttpGet("HoldedDuplicates")]
    public async Task<IActionResult> HoldedDuplicates(CancellationToken ct)
    {
        var plan = await holdedConnector.GetHoldedDuplicatesAsync(ct);
        return View(plan);
    }

    /// <summary>No <c>CancellationToken</c>: a run that has deleted one document finishes the rest.</summary>
    [HttpPost("HoldedDuplicates/Apply")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyHoldedDuplicates()
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;

        var result = await holdedConnector.RemoveHoldedDuplicatesAsync(actorUserId);
        return View("HoldedDuplicatesApplied", result);
    }

    /// <summary>Books a whole file against the one Sabadell line the bank debited it as. No
    /// <c>CancellationToken</c>, as for a single booking.</summary>
    [HttpPost("Sepa/BookFile")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookSepaFile(
        [BindRequired] Guid fileId, [BindRequired] string bankMovementId,
        int days = IFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
        if (!ModelState.IsValid) return MalformedMutation(actorUserId);

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
        [BindRequired] Guid fileId, int days = IFinanceAdminService.DefaultSepaMatchToleranceDays)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
        if (!ModelState.IsValid) return MalformedMutation(actorUserId);

        var result = await holdedConnector.BookSepaFileMatchesAsync(fileId, actorUserId, days);
        if (result.Succeeded) SetSuccess(result.Message); else SetError(result.Message);

        return RedirectToAction(nameof(Sepa), new { days });
    }

    private BadRequestObjectResult MalformedMutation(Guid actorUserId)
    {
        logger.LogWarning("Rejected malformed Finance {Action} form for actor {ActorUserId}",
            ControllerContext.ActionDescriptor.ActionName, actorUserId);
        return BadRequest(ModelState);
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
        var ledger = await finance.GetCreditorLedgerAsync(accountNum, HttpContext.RequestAborted);
        if (ledger is null) return NotFound();

        // The finance-admin review queue is every submitted report; the ones booked to this account are the other
        // side of the ledger.
        var reports = (await expenseReports.GetReviewQueueAsync(viewerUserId, isFinanceAdmin: true, HttpContext.RequestAborted))
            .Where(r => r.HoldedSupplierAccountNum == accountNum)
            .OrderByDescending(r => r.ApprovedAt ?? r.SubmittedAt ?? r.CreatedAt)
            .ToList();

        // The running balance is the service's; the page shows newest activity first.
        var memberName = string.IsNullOrWhiteSpace(ledger.Contact?.Name) ? "member" : ledger.Contact.Name;
        var lines = StatementRows(ledger.Lines, memberName)
            .OrderByDescending(r => r.Line.Date)
            .ThenByDescending(r => r.Line.EntryNumber)
            .ThenBy(r => r.Line.Line)
            .ToList();
        var match = await creditorMatches.GetAsync(accountNum, viewerUserId, HttpContext.RequestAborted);
        var (transfers, _) = await finance.GetSepaTransfersAsync(HttpContext.RequestAborted);
        var sepa = transfers
            .Where(t => t.SupplierAccountNum == accountNum)
            .OrderByDescending(t => t.GeneratedAt)
            .ToList();
        return View(new CreditorStatementVm(ledger, lines, reports, match, sepa));
    }

    // Holded books one transfer as a payment line per purchase it settles. We pay a member at most once a day, so
    // consecutive same-day, same-direction payment lines are one transfer and show as one row; requiring them to be
    // consecutive keeps each row's running balance true.
    private static List<CreditorStatementLineVm> StatementRows(IEnumerable<CreditorLedgerLine> lines, string memberName)
    {
        var groups = new List<List<CreditorLedgerLine>>();
        foreach (var line in lines.OrderBy(l => l.Date).ThenBy(l => l.EntryNumber).ThenBy(l => l.Line))
        {
            var last = groups.Count > 0 ? groups[^1][^1] : null;
            if (last is not null && IsPayment(line) && IsPayment(last)
                && line.Date.InZone(MadridZone).Date == last.Date.InZone(MadridZone).Date
                && line.Debit > 0 == last.Debit > 0)
                groups[^1].Add(line);
            else
                groups.Add([line]);
        }
        return groups.Select(g => new CreditorStatementLineVm(g, StatementDescription(g, memberName))).ToList();
    }

    private static bool IsPayment(CreditorLedgerLine line) =>
        string.Equals(line.Type, "payment", StringComparison.OrdinalIgnoreCase);

    // Holded labels a payment with the purchase it settles, which reads like a second purchase.
    private static string? StatementDescription(List<CreditorLedgerLine> group, string memberName)
    {
        var line = group[0];
        if (!IsPayment(line))
            return line.Description;
        var description = line.Debit > 0
            ? $"Payment: NCA → {memberName}"
            : $"Payment: {memberName} → NCA";
        return group.Count > 1 ? $"{description} ({group.Count} purchases)" : description;
    }

    [HttpPost("Creditors/Bind")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BindCreditor([BindRequired] Guid userId, [BindRequired] int supplierAccountNum, string? returnUrl)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
        if (!ModelState.IsValid) return MalformedMutation(actorUserId);
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
    public async Task<IActionResult> UnbindCreditor([BindRequired] Guid userId, string? returnUrl)
    {
        var actor = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (actor is null) return Challenge();
        var actorUserId = actor.Id;
        if (!ModelState.IsValid) return MalformedMutation(actorUserId);
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
            if (await holded.SyncPurchaseDocumentsAsync(HttpContext.RequestAborted))
                SetSuccess("Synced the purchase documents from Holded.");
            else
                SetInfo("A purchase-document sync was already running, so this one was skipped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Holded sync failed");
            SetError("Sync failed.");
        }
        return RedirectToAction(nameof(HoldedUnmatched));
    }
}
