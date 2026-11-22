using Humans.Base.Controllers;
using Humans.Finance.Contracts;
using Humans.Pleo.Models;
using Humans.Pleo.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Pleo.Controllers;

[Authorize(Policy = FinancePolicies.Read)]
[Route("Pleo")]
internal sealed class PleoController(
    IUserService userService,
    IPleoAdminService pleo,
    ILogger<PleoController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var s = await pleo.GetSummaryAsync(ct);
        return View(s with
        {
            ByAccount = s.ByAccount.OrderBy(a => a.Key, StringComparer.Ordinal).ToList(),
            ByCardholder = s.ByCardholder.OrderByDescending(c => c.Spend).ToList(),
        });
    }

    [HttpPost("SyncNow")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        try
        {
            switch (await pleo.SyncAsync(ct))
            {
                case PleoSyncOutcome.Synced: SetSuccess("Pleo mirror refreshed."); break;
                case PleoSyncOutcome.SyncedWithoutCategories:
                    SetError("Pleo mirror refreshed, but categories could not be fetched; the error is on the sync card.");
                    break;
                case PleoSyncOutcome.Busy: SetInfo("A sweep was already running, so this one was skipped."); break;
                default: SetError("Pleo is not configured."); break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pleo sweep failed");
            SetError("Sync failed; the error is on the sync card.");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Entries")]
    public async Task<IActionResult> Entries(
        string? account, string? cardholder, string? family, bool noReceipt = false, bool unmapped = false,
        CancellationToken ct = default)
    {
        var page = await pleo.GetEntriesPageAsync(
            new PleoEntriesFilter(account, cardholder, family, noReceipt, unmapped), ct);
        return View(page with
        {
            Rows = page.Rows.OrderByDescending(r => r.PerformedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList(),
            Accounts = page.Accounts.OrderBy(a => a.Value, StringComparer.Ordinal).ToList(),
            Cardholders = page.Cardholders.OrderBy(c => c.Label, StringComparer.Ordinal).ToList(),
            Families = page.Families.Order(StringComparer.Ordinal).ToList(),
        });
    }

    [HttpGet("Entries/{id}")]
    public async Task<IActionResult> Entry(string id, CancellationToken ct)
    {
        var entry = await pleo.GetEntryViewAsync(id, ct);
        return entry is null ? NotFound() : View(entry);
    }

    [HttpGet("Entries/{id}/Receipts/{receiptId}")]
    public async Task<IActionResult> Receipt(string id, string receiptId, CancellationToken ct)
    {
        var file = await pleo.GetReceiptFileAsync(id, receiptId, ct);
        if (file is null) return NotFound();
        // The site denies all framing; the entry page embeds this one file, so it alone may be framed by its own origin.
        Response.Headers.XFrameOptions = "SAMEORIGIN";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'self'";
        return File(file.Content, file.ContentType);
    }

    [HttpGet("Categories")]
    public async Task<IActionResult> Categories(string? name = null, string? holdedCode = null, CancellationToken ct = default)
    {
        var page = await pleo.GetCategoriesPageAsync(ct);
        ViewData["AddName"] = name;
        ViewData["AddCode"] = holdedCode;
        return View(page with
        {
            Unlinked = page.Unlinked.OrderByDescending(u => u.Spend).ToList(),
            Categories = page.Categories.OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
            UnlinkedNames = page.UnlinkedNames.OrderBy(n => n.Label, StringComparer.Ordinal).ToList(),
            ActiveAccounts = page.ActiveAccounts.OrderBy(a => a.Value, StringComparer.Ordinal).ToList(),
        });
    }

    [HttpPost("Categories/Link")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Link(string? pleoCategoryId, Guid categoryId, CancellationToken ct) =>
        CategoryActionAsync((userId, c) => pleo.LinkCategoryAsync(pleoCategoryId ?? "", categoryId, userId, c),
            "Category linked.", ct);

    [HttpPost("Categories/Unlink")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Unlink(Guid categoryId, CancellationToken ct) =>
        CategoryActionAsync((userId, c) => pleo.UnlinkCategoryAsync(categoryId, userId, c), "Category unlinked.", ct);

    [HttpPost("Categories/Add")]
    [Authorize(Policy = FinancePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Add(string name, string holdedCode, CancellationToken ct) =>
        CategoryActionAsync((userId, c) => pleo.AddCategoryAsync(name ?? "", holdedCode ?? "", userId, c),
            "Category added.", ct, onError: new { name, holdedCode });

    /// <summary>A refused action redirects with <c>onError</c> as route values, which the Categories page refills its form from.</summary>
    private async Task<IActionResult> CategoryActionAsync(
        Func<Guid, CancellationToken, Task<string?>> action, string success, CancellationToken ct,
        object? onError = null)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();
        if (await action(userId, ct) is { } error)
        {
            SetError(error);
            return RedirectToAction(nameof(Categories), onError);
        }
        SetSuccess(success);
        return RedirectToAction(nameof(Categories));
    }
}
