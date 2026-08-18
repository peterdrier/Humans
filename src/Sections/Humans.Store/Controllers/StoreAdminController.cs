using Humans.Shifts.Contracts;
using Humans.Store.Services;
using Humans.Store.Services.Dtos;
using Humans.Store.Models;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Store.Controllers;

[Authorize(Policy = PolicyNames.StoreCatalogAdmin)]
[Route("Store/Admin")]
internal sealed class StoreAdminController(
    Service storeService,
    IBurnSettingsService burnSettings,
    IClock clock,
    IUserServiceRead userService,
    ILogger<StoreAdminController> logger) : HumansControllerBase(userService)
{
    private const decimal SpanishStandardVatRatePercent = 21m;

    [HttpGet("Catalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var products = (await storeService.GetAllProductsForYearAsync(year, ct))
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        return View(new CatalogAdminViewModel { Year = year, Products = products });
    }

    [HttpGet("Summary")]
    public async Task<IActionResult> Summary(int? year, CancellationToken ct)
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        var defaultYear = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var selectedYear = year ?? defaultYear;

        var summary = await storeService.GetStoreSummaryAsync(selectedYear, ct);
        return View(new SummaryViewModel { Summary = summary });
    }

    [HttpGet("Payments")]
    public async Task<IActionResult> Payments(CancellationToken ct)
    {
        var report = await storeService.GetStripeReconciliationAsync(ct);
        var rows = report.Rows
            .OrderByDescending(r => r.Status is StripeReconciliationStatus.Missing or StripeReconciliationStatus.Unmatched)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();
        return View(new PaymentsReconciliationViewModel { Report = report, Rows = rows });
    }

    [HttpPost("Payments/RecordMissing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordMissingPayments(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var result = await storeService.RecordMissingStripePaymentsAsync(user.Id, ct);
        if (result.RecordedCount > 0)
            SetSuccess($"Recorded {result.RecordedCount} Stripe payment(s) totalling €{result.TotalEur:0.00}.");
        else
            SetInfo("No missing Stripe payments to record — Stripe and the Store ledger are already reconciled.");
        return RedirectToAction(nameof(Payments));
    }

    [HttpGet("Catalog/Edit")]
    public async Task<IActionResult> Edit(CancellationToken ct)
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var model = new ProductInputModel
        {
            Year = year,
            VatRatePercent = SpanishStandardVatRatePercent,
            OrderableUntil = $"{year}-12-31",
            IsActive = true
        };
        return View("CatalogEdit", model);
    }

    [HttpGet("Catalog/Edit/{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var p = await storeService.GetProductAsync(id, ct);
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
            OrderableUntil = LocalDatePattern.Iso.Format(p.OrderableUntil),
            IsActive = p.IsActive
        };
        return View("CatalogEdit", model);
    }

    [HttpPost("Catalog/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ProductInputModel input, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
            return View("CatalogEdit", input);

        var result = await storeService.SaveProductWithResultAsync(
            new ProductSaveRequest(
                input.Id,
                input.Year,
                input.Name,
                input.Description,
                input.UnitPriceEur,
                input.VatRatePercent,
                input.DepositAmountEur,
                input.OrderableUntil,
                input.IsActive),
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

    [HttpPost("Catalog/Deactivate/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await storeService.DeactivateProductAsync(id, user.Id, ct);
            SetSuccess("Product deactivated.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Store catalog deactivate rejected: {Reason}", ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Catalog));
    }
}
