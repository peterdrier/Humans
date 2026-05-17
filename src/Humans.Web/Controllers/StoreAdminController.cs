using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Store;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.StoreCatalogAdmin)]
[Route("Store/Admin")]
public class StoreAdminController : HumansControllerBase
{
    private const decimal SpanishStandardVatRatePercent = 21m;

    private readonly IStoreService _storeService;
    private readonly IShiftManagementService _shifts;
    private readonly IClock _clock;
    private readonly ILogger<StoreAdminController> _logger;

    public StoreAdminController(
        IStoreService storeService,
        IShiftManagementService shifts,
        IClock clock,
        IUserService userService,
        ILogger<StoreAdminController> logger)
        : base(userService)
    {
        _storeService = storeService;
        _shifts = shifts;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("Catalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        var activeEvent = await _shifts.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : _clock.GetCurrentInstant().InUtc().Year;
        var products = (await _storeService.GetAllProductsForYearAsync(year, ct))
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        return View(new StoreCatalogAdminViewModel { Year = year, Products = products });
    }

    [HttpGet("Catalog/Edit")]
    public async Task<IActionResult> Edit(CancellationToken ct)
    {
        var activeEvent = await _shifts.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : _clock.GetCurrentInstant().InUtc().Year;
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
        var p = await _storeService.GetProductAsync(id, ct);
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

        var result = await _storeService.SaveProductWithResultAsync(
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
            await _storeService.DeactivateProductAsync(id, user.Id, ct);
            SetSuccess("Product deactivated.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Store catalog deactivate rejected: {Reason}", ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Catalog));
    }
}
