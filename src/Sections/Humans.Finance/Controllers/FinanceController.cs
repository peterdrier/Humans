using Humans.Finance.Contracts;
using Humans.Finance.Models;
using Humans.UI.Authorization;
using Humans.UI.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Finance.Controllers;

/// <summary>
/// Finance's own pages: Holded account provisioning, the unmatched-document queue and the
/// creditor-account admin (<c>/Finance/HoldedAccounts</c>, <c>/Finance/HoldedUnmatched</c>,
/// <c>/Finance/Creditors*</c>, <c>/Finance/HoldedSync/Run</c>).
/// </summary>
/// <remarks>
/// The other 23 actions that used to share this class are Budget CRUD — years, groups,
/// categories, line items, cash flow, audit log — and are now
/// <c>Humans.Budget</c>'s <c>BudgetAdminController</c>, keeping the same <c>[Route("Finance")]</c>
/// prefix so no URL moved. Dragging them in here would have put Budget's whole admin surface
/// inside the Finance section (nobodies-collective/Humans#866, G5).
/// </remarks>
[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
[Route("Finance")]
internal sealed class FinanceController(
    IUserServiceRead userService,
    IHoldedFinanceService holdedFinance,
    ILogger<FinanceController> logger) : HumansControllerBase(userService)
{
    [HttpGet("HoldedAccounts")]
    public async Task<IActionResult> HoldedAccounts(int blockStart = 62900100)
    {
        var plan = await holdedFinance.GetProvisioningPlanAsync(blockStart);
        ViewBag.BlockStart = blockStart;
        return View(plan);
    }

    [HttpPost("HoldedAccounts/Provision")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionHoldedAccounts(int blockStart, bool addAll)
    {
        try
        {
            var n = await holdedFinance.ProvisionAsync(blockStart, addAll);
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
        => View(await holdedFinance.GetUnmatchedAsync());

    [HttpGet("Creditors")]
    public async Task<IActionResult> Creditors(string? sort, string? dir)
    {
        var (rows, unresolved) = await holdedFinance.ListCreditorAccountsAsync();

        var names = new Dictionary<Guid, string>();
        var boundIds = rows.SelectMany(r => r.Bindings).Select(b => b.UserId)
            .Concat(unresolved.Select(b => b.UserId))
            .Distinct().ToList();
        if (boundIds.Count > 0)
            foreach (var kv in await UserService.GetUserInfosAsync(boundIds))
                names[kv.Key] = kv.Value.BurnerName;

        // The page shows one balance, inverted so a positive figure is money owed to the member.
        // Only the display flips: the contract row keeps Holded's Σdebit − Σcredit.
        var vms = rows
            .Select(r => new CreditorAccountRowVm(
                r.SupplierAccountNum, r.Name, r.Balance is { } bal ? -bal : null,
                r.Bindings.Select(b => new CreditorBindingVm(
                    b.UserId,
                    names.TryGetValue(b.UserId, out var nm) ? nm : b.UserId.ToString(),
                    b.Source.ToString())).ToList()))
            .ToList();

        var sortBy = sort is "name" or "balance" or "member" ? sort : "account";
        var sortDir = string.Equals(dir, "desc", StringComparison.Ordinal) ? "desc" : "asc";
        var accounts = SortCreditorRows(vms, sortBy, sortDir);

        var unresolvedVm = unresolved
            .Select(b => new CreditorBindingVm(
                b.UserId,
                names.TryGetValue(b.UserId, out var nm) ? nm : b.UserId.ToString(),
                b.Source.ToString()))
            .ToList();

        return View(new CreditorsPageVm(accounts, unresolvedVm, sortBy, sortDir));
    }

    /// <summary>Every column on /Finance/Creditors is sortable; ties resolve on the account number
    /// so the order is stable across reloads.</summary>
    private static List<CreditorAccountRowVm> SortCreditorRows(
        IReadOnlyList<CreditorAccountRowVm> rows, string sortBy, string sortDir)
    {
        Func<CreditorAccountRowVm, IComparable> key = sortBy switch
        {
            "name" => r => r.Name.ToUpperInvariant(),
            "balance" => r => r.Balance ?? 0m,
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
        var ledger = await holdedFinance.GetCreditorLedgerAsync(accountNum);
        if (ledger is null) return NotFound();

        // Controllers sort for display: newest activity first.
        ViewBag.Lines = ledger.Lines
            .OrderByDescending(l => l.Date)
            .ThenByDescending(l => l.EntryNumber)
            .ThenBy(l => l.Line)
            .ToList();
        return View(ledger);
    }

    [HttpPost("Creditors/Bind")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BindCreditor(Guid userId, int supplierAccountNum, string? returnUrl)
    {
        try
        {
            var result = await holdedFinance.SetCreditorContactAsync(userId, supplierAccountNum);
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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnbindCreditor(Guid userId, string? returnUrl)
    {
        try
        {
            if (await holdedFinance.ClearCreditorContactAsync(userId))
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
