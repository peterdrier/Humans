using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Authorization.Requirements;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Store")]
public class StoreController : HumansControllerBase
{
    private readonly IStoreService _storeService;
    private readonly ICampService _campService;
    private readonly IShiftManagementService _shifts;
    private readonly IAuthorizationService _authService;
    private readonly IClock _clock;
    private readonly ILogger<StoreController> _logger;

    public StoreController(
        IStoreService storeService,
        ICampService campService,
        IShiftManagementService shifts,
        IAuthorizationService authService,
        IClock clock,
        UserManager<User> userManager,
        ILogger<StoreController> logger)
        : base(userManager)
    {
        _storeService = storeService;
        _campService = campService;
        _shifts = shifts;
        _authService = authService;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var year = await ResolveActiveYearAsync();
        var catalog = await _storeService.GetActiveCatalogAsync(year, ct);

        var isPrivilegedReader = User.IsInRole(RoleNames.Admin)
            || User.IsInRole(RoleNames.FinanceAdmin)
            || User.IsInRole(RoleNames.StoreAdmin);

        var leadCamps = await _campService.GetCampsByLeadUserIdAsync(user.Id, ct);
        var sections = new List<StoreCampSeasonOrders>();
        foreach (var camp in leadCamps)
        {
            var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
            if (season is null) continue;
            var orders = await _storeService.GetOrdersForCampSeasonAsync(season.Id, ct);
            sections.Add(new StoreCampSeasonOrders(season.Id, season.Name, year, orders));
        }

        if (sections.Count == 0 && !isPrivilegedReader)
        {
            SetInfo("You don't lead any camps this year, so there are no Store orders to manage.");
        }

        var model = new StoreIndexViewModel
        {
            Year = year,
            Catalog = catalog,
            CampSeasons = sections
        };
        return View(model);
    }

    [HttpGet("Order/{id:guid}")]
    public async Task<IActionResult> Order(Guid id, CancellationToken ct)
    {
        var (errorResult, _) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await _storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var view = await _authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.View);
        if (!view.Succeeded) return Forbid();

        var year = await ResolveActiveYearAsync();
        var catalog = await _storeService.GetActiveCatalogAsync(year, ct);
        var season = await _campService.GetCampSeasonByIdAsync(order.CampSeasonId, ct);

        var canEdit = (await _authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.AddLine)).Succeeded;

        var model = new StoreOrderViewModel
        {
            Order = order,
            Catalog = catalog,
            CampName = season?.Name ?? "(unknown camp)",
            CanEdit = canEdit
        };
        return View(model);
    }

    [HttpPost("Order/Create/{campSeasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Guid campSeasonId, string? label, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var season = await _campService.GetCampSeasonByIdAsync(campSeasonId, ct);
        if (season is null) return NotFound();

        var auth = await _authService.AuthorizeAsync(
            User,
            new StoreOrderCreateContext(campSeasonId),
            StoreOrderOperationRequirement.Create);
        if (!auth.Succeeded) return Forbid();

        var newId = await _storeService.CreateOrderAsync(campSeasonId, label, user.Id, ct);
        SetSuccess("Order created.");
        return RedirectToAction(nameof(Order), new { id = newId });
    }

    [HttpPost("Order/{id:guid}/AddLine")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLine(Guid id, Guid productId, int qty, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await _storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await _authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.AddLine);
        if (!auth.Succeeded) return Forbid();

        try
        {
            await _storeService.AddLineAsync(id, productId, qty, user.Id, ct);
            SetSuccess("Line added.");
        }
        catch (ArgumentException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Order), new { id });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation(ex, "AddLine rejected for order {OrderId}", id);
            SetError(ex.Message);
            return RedirectToAction(nameof(Order), new { id });
        }
        return RedirectToAction(nameof(Order), new { id });
    }

    [HttpPost("Order/{id:guid}/RemoveLine")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLine(Guid id, Guid lineId, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await _storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await _authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.RemoveLine);
        if (!auth.Succeeded) return Forbid();

        try
        {
            await _storeService.RemoveLineAsync(id, lineId, user.Id, ct);
            SetSuccess("Line removed.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation(ex, "RemoveLine rejected for line {LineId}", lineId);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Order), new { id });
    }

    [HttpPost("Order/{id:guid}/UpdateCounterparty")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCounterparty(
        Guid id,
        OrderCounterpartyInput input,
        CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await _storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await _authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.EditCounterparty);
        if (!auth.Succeeded) return Forbid();

        try
        {
            await _storeService.UpdateCounterpartyAsync(id, input, user.Id, ct);
            SetSuccess("Counterparty updated.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation(ex, "UpdateCounterparty rejected for order {OrderId}", id);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Order), new { id });
    }

    private async Task<int> ResolveActiveYearAsync()
    {
        var activeEvent = await _shifts.GetActiveAsync();
        return activeEvent?.Year > 0 ? activeEvent.Year : _clock.GetCurrentInstant().InUtc().Year;
    }
}
