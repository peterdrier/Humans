using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Web.Authorization;
using Humans.Web.Authorization.Requirements;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Store")]
public class StoreController(
    IStoreService storeService,
    ICampServiceRead campService,
    IAuthorizationService authService,
    IUserServiceRead userService,
    ILogger<StoreController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var isPrivilegedReader = RoleChecks.CanAdministerStore(User);
        var pageData = await storeService.GetIndexDataAsync(user.Id, isPrivilegedReader, ct);
        if (pageData.ShowNoCampOrdersMessage)
        {
            SetInfo("You don't lead any camps this year, so there are no Store orders to manage.");
        }

        var model = new StoreIndexViewModel
        {
            Year = pageData.Year,
            Catalog = pageData.Catalog,
            CampSeasons = pageData.CampSeasons
        };
        return View(model);
    }

    [HttpGet("Order/{id:guid}")]
    public async Task<IActionResult> Order(Guid id, CancellationToken ct)
    {
        var (errorResult, _) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var view = await authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.View);
        if (!view.Succeeded) return Forbid();

        var canEdit = (await authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.AddLine)).Succeeded;
        var canPay = (await authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.Pay)).Succeeded;
        var pageData = await storeService.GetOrderPageDataAsync(order, canEdit, canPay, ct);
        return View(StoreOrderViewModel.FromPageData(pageData));
    }

    [HttpPost("Order/{id:guid}/Pay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(Guid id, decimal amountEur, CancellationToken ct)
    {
        var (errorResult, _) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.Pay);
        if (!auth.Succeeded) return Forbid();

        var orderUrl = Url.Action(nameof(Order), "Store", new { id }, Request.Scheme, Request.Host.Value)
            ?? throw new InvalidOperationException("Failed to compute order URL.");

        try
        {
            var sessionUrl = await storeService.CreateStripeCheckoutSessionAsync(order, amountEur, orderUrl, ct);
            return Redirect(sessionUrl);
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Order), new { id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe Checkout Session creation failed for order {OrderId}", id);
            SetError("Could not start Stripe checkout. Please try again or contact an admin.");
            return RedirectToAction(nameof(Order), new { id });
        }
    }

    [HttpPost("Order/Create/{campSeasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Guid campSeasonId, string? label, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var season = await campService.GetCampSeasonByIdAsync(campSeasonId, ct);
        if (season is null) return NotFound();

        var auth = await authService.AuthorizeAsync(
            User,
            new StoreOrderCreateContext(campSeasonId),
            StoreOrderOperationRequirement.Create);
        if (!auth.Succeeded) return Forbid();

        var newId = await storeService.CreateOrderAsync(campSeasonId, label, user.Id, ct);
        SetSuccess("Order created.");
        return RedirectToAction(nameof(Order), new { id = newId });
    }

    [HttpPost("Order/{id:guid}/AddLine")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLine(Guid id, Guid productId, int qty, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.AddLine);
        if (!auth.Succeeded) return Forbid();

        var result = await storeService.AddLineWithResultAsync(id, productId, qty, user.Id, ct);
        if (!result.Succeeded)
            SetError(result.ErrorMessage ?? "Could not add line.");
        else
            SetSuccess("Line added.");

        return RedirectToAction(nameof(Order), new { id });
    }

    [HttpPost("Order/{id:guid}/RemoveLine")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLine(Guid id, Guid lineId, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.RemoveLine);
        if (!auth.Succeeded) return Forbid();

        var result = await storeService.RemoveLineWithResultAsync(id, lineId, user.Id, ct);
        if (!result.Succeeded)
            SetError(result.ErrorMessage ?? "Could not remove line.");
        else
            SetSuccess("Line removed.");

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

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.EditCounterparty);
        if (!auth.Succeeded) return Forbid();

        var result = await storeService.UpdateCounterpartyWithResultAsync(id, input, user.Id, ct);
        if (!result.Succeeded)
            SetError(result.ErrorMessage ?? "Could not update counterparty.");
        else
            SetSuccess("Counterparty updated.");

        return RedirectToAction(nameof(Order), new { id });
    }

}
