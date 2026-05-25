using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Store;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Web.Models.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.StoreCatalogAdmin)]
[Route("Store/Admin")]
public class StoreAdminController(
    IStoreService storeService,
    IShiftManagementService shifts,
    IClock clock,
    IUserServiceRead userService,
    ILogger<StoreAdminController> logger) : HumansControllerBase(userService)
{
    private const decimal SpanishStandardVatRatePercent = 21m;

    [HttpGet("Catalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        var activeEvent = await shifts.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var products = (await storeService.GetAllProductsForYearAsync(year, ct))
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        return View(new StoreCatalogAdminViewModel { Year = year, Products = products });
    }

    [HttpGet("Summary")]
    public async Task<IActionResult> Summary(int? year, CancellationToken ct)
    {
        var activeEvent = await shifts.GetActiveAsync();
        var defaultYear = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var selectedYear = year ?? defaultYear;

        var summary = await storeService.GetStoreSummaryAsync(selectedYear, ct);
        return View(new StoreSummaryViewModel { Summary = summary });
    }

    [HttpGet("Catalog/Edit")]
    public async Task<IActionResult> Edit(CancellationToken ct)
    {
        var activeEvent = await shifts.GetActiveAsync();
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
            new StoreProductSaveRequest(
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
