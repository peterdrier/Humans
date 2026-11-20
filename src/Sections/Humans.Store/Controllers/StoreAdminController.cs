using Humans.Store.Contracts;
using Humans.Store.Services;
using Humans.Store.Services.Dtos;
using Humans.Store.Models;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Text;

using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Store.Controllers;

[Authorize(Policy = StorePolicies.Read)]
[Route("Store/Admin")]
internal sealed class StoreAdminController(
    Service storeService,
    CatalogService catalogService,
    IUserService userService) : HumansControllerBase(userService)
{
    private const decimal SpanishStandardVatRatePercent = 21m;

    [HttpGet("Catalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        var year = storeService.GetCurrentEventYear();
        var products = (await catalogService.GetAllProductsForYearAsync(year, ct))
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        return View(new CatalogAdminViewModel { Year = year, Products = products });
    }

    [HttpGet("Summary")]
    public async Task<IActionResult> Summary(int? year, CancellationToken ct)
    {
        var defaultYear = storeService.GetCurrentEventYear();
        var selectedYear = year ?? defaultYear;

        var summary = await storeService.GetStoreSummaryAsync(selectedYear, ct);
        return View(new SummaryViewModel { Summary = summary });
    }

    [HttpGet("Payments")]
    public async Task<IActionResult> Payments(CancellationToken ct)
    {
        var report = await storeService.GetStripeReconciliationAsync(ct);
        var rows = report.Rows
            .OrderByDescending(r => r.RequiresReview)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();
        return View(new PaymentsReconciliationViewModel { Report = report, Rows = rows });
    }

    [HttpGet("Transfers")]
    public async Task<IActionResult> Transfers(
        int? year, [FromServices] StoreTransferBooking transferBooking, CancellationToken ct)
    {
        var selectedYear = year ?? storeService.GetCurrentEventYear();
        return View(await transferBooking.GetPlanAsync(selectedYear, ct));
    }

    [HttpPost("Transfers/Book")]
    [Authorize(Policy = StorePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookTransfers(
        int year, [FromServices] StoreTransferBooking transferBooking, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var (lines, refused) = await transferBooking.BookAsync(year, user.Id, ct);
        var posted = lines.Count(l => l.Outcome != TransferOutcome.Failed);
        var failed = lines.Where(l => l.Outcome == TransferOutcome.Failed).ToList();
        if (refused is not null) SetError(refused + (lines.Count > 0 ? $" {posted} entr(ies) posted first." : ""));
        else if (failed.Count > 0)
            SetError($"Posted {posted}; {failed.Count} failed: "
                + string.Join("; ", failed.Select(f => $"{f.Row.TeamName} / {f.Row.ProductName}: {f.Error}")));
        else if (posted > 0) SetSuccess($"Posted {posted} transfer entr(ies) to Holded.");
        else SetInfo("Nothing was ready to book.");
        return RedirectToAction(nameof(Transfers), new { year });
    }

    [HttpGet("OrderYears")]
    public async Task<IActionResult> OrderYears(CancellationToken ct)
    {
        var report = await storeService.GetOrderYearRepairReportAsync(ct);
        return View(report);
    }

    [HttpPost("OrderYears/Repair")]
    [Authorize(Policy = StorePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RepairOrderYears(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var repaired = await storeService.RepairOrderYearsAsync(user.Id, ct);
        if (repaired == 0)
            SetInfo("No resolvable legacy order years remain.");
        else
            SetSuccess($"Repaired {repaired} legacy order year(s).");
        return RedirectToAction(nameof(OrderYears));
    }

    [HttpGet("PaymentMethods")]
    public async Task<IActionResult> PaymentMethods(CancellationToken ct)
    {
        var rows = await storeService.GetPaymentMethodRepairRowsAsync(ct);
        return View(rows);
    }

    [HttpPost("PaymentMethods/Repair")]
    [Authorize(Policy = StorePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RepairPaymentMethods(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var repaired = await storeService.RepairPaymentMethodNamesAsync(user.Id, ct);
        if (repaired == 0)
            SetInfo("No payments need their method copied.");
        else
            SetSuccess($"Copied the method on {repaired} payment(s).");
        return RedirectToAction(nameof(PaymentMethods));
    }

    [HttpPost("Payments/RecordMissing")]
    [Authorize(Policy = StorePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordMissingPayments(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await storeService.RecordMissingStripePaymentsAsync(user.Id, ct);
        if (result.RecordedCount > 0)
            SetSuccess($"Recorded {result.RecordedCount} Stripe payment(s) totalling €{result.TotalEur:0.00}.");
        else
            SetInfo("No missing Stripe payments to record — Stripe and the Store ledger are already reconciled.");
        return RedirectToAction(nameof(Payments));
    }

    [HttpGet("Catalog/Edit")]
    [Authorize(Policy = StorePolicies.Manage)]
    public async Task<IActionResult> Edit(CancellationToken ct)
    {
        var year = storeService.GetCurrentEventYear();
        var model = new ProductInputModel
        {
            Year = year,
            VatRatePercent = SpanishStandardVatRatePercent,
            OrderableUntil = $"{year}-12-31",
            IsActive = true,
            AccountChoices = await catalogService.GetAccountChoicesAsync(ct)
        };
        return View("CatalogEdit", model);
    }

    [HttpGet("Catalog/Edit/{id:guid}")]
    [Authorize(Policy = StorePolicies.Manage)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var p = await catalogService.GetProductAsync(id, ct);
        if (p is null) return NotFound();

        var model = new ProductInputModel
        {
            Id = p.Id,
            Year = p.Year,
            Name = p.Name,
            Description = p.Description,
            UnitPriceEur = p.UnitPriceEur,
            VatRatePercent = p.VatRatePercent,
            DepositAmountEur = p.DepositAmountEur,
            HoldedRevenueAccountNum = p.HoldedRevenueAccountNum,
            HoldedExpenseAccountNum = p.HoldedExpenseAccountNum,
            HoldedTransferAccountNum = p.HoldedTransferAccountNum,
            OrderableUntil = LocalDatePattern.Iso.Format(p.OrderableUntil),
            IsActive = p.IsActive,
            AccountChoices = await catalogService.GetAccountChoicesAsync(ct)
        };
        return View("CatalogEdit", model);
    }

    [HttpPost("Catalog/Save")]
    [Authorize(Policy = StorePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ProductInputModel input, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        input.AccountChoices = await catalogService.GetAccountChoicesAsync(ct);
        if (!ModelState.IsValid)
            return View("CatalogEdit", input);

        var result = await catalogService.SaveProductWithResultAsync(
            new ProductSaveRequest(
                input.Id,
                input.Year,
                input.Name,
                input.Description,
                input.UnitPriceEur,
                input.VatRatePercent,
                input.DepositAmountEur,
                input.OrderableUntil,
                input.IsActive,
                input.HoldedRevenueAccountNum,
                input.HoldedExpenseAccountNum,
                input.HoldedTransferAccountNum),
            user.Id,
            ct);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(result.ErrorField ?? string.Empty, result.ErrorMessage ?? "Could not save product.");
            return View("CatalogEdit", input);
        }

        SetSuccess(result.Created ? "Product created." : "Product updated.");
        return RedirectToAction(nameof(Catalog));
    }

    [HttpGet("Catalog/Accounts")]
    [Authorize(Policy = StorePolicies.Manage)]
    public async Task<IActionResult> Accounts(int? year, CancellationToken ct)
    {
        var selectedYear = year ?? storeService.GetCurrentEventYear();
        return View("CatalogAccounts", await BuildAccountsViewAsync(selectedYear, posted: null, ct));
    }

    [HttpPost("Catalog/Accounts")]
    [Authorize(Policy = StorePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAccounts(int year, List<CatalogAccountsInput> rows, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await catalogService.SaveAccountsAsync(
            year, rows.Select(r => new ProductAccounts(r.ProductId, r.Income, r.Cost, r.TeamUsage)).ToList(), user.Id, ct);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Refusal!);
            return View("CatalogAccounts", await BuildAccountsViewAsync(year, rows, ct));
        }

        SetSuccess("Accounts saved.");
        return RedirectToAction(nameof(Accounts), new { year });
    }

    private async Task<CatalogAccountsViewModel> BuildAccountsViewAsync(
        int year, IReadOnlyList<CatalogAccountsInput>? posted, CancellationToken ct)
    {
        var products = (await catalogService.GetAllProductsForYearAsync(year, ct))
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        var postedById = (posted ?? []).GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.First());
        return new CatalogAccountsViewModel
        {
            Year = year,
            Choices = await catalogService.GetAccountChoicesAsync(ct),
            Rows = products.Select((p, i) => postedById.TryGetValue(p.Id, out var r)
                    ? new CatalogAccountsRow(i, p, r.Income, r.Cost, r.TeamUsage)
                    : new CatalogAccountsRow(i, p, p.HoldedRevenueAccountNum, p.HoldedExpenseAccountNum, p.HoldedTransferAccountNum))
                .ToList()
        };
    }

    /// <summary>Posted from the Store tab on /Settings.</summary>
    [HttpPost("Settings")]
    [Authorize(Policy = StorePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(StoreSettingsViewModel model, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = ModelState.IsValid
            ? await catalogService.SaveSettingsAsync(
                new StoreSettingsDto(model.StripeGatewayTreasuryId, model.SimplifiedInvoiceThresholdEur), user.Id, ct)
            : AdminMutationResult.Refused("Invalid store settings.");
        if (result.Succeeded)
            SetSuccess("Store settings saved.");
        else
            SetError(result.Refusal!);
        return Redirect("/Settings#store");
    }

    [HttpPost("Catalog/Deactivate/{id:guid}")]
    [Authorize(Policy = StorePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await catalogService.DeactivateProductAsync(id, user.Id, ct);
        if (result.Succeeded)
            SetSuccess("Product deactivated.");
        else
            SetError(result.Refusal!);
        return RedirectToAction(nameof(Catalog));
    }
}
