using Humans.Camps.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Services;
using Humans.Store.Services.Dtos;
using Humans.Base.Authorization;
using Humans.Store.Authorization;
using Humans.Store.Models;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

using Humans.Users.Contracts;

namespace Humans.Store.Controllers;

[Authorize]
[Route("Store")]
internal sealed class StoreController(
    Service storeService,
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

        // Full store admins and TeamsAdmins read every counterparty; TeamsAdmins
        // can edit team orders but only view camp orders, so per-row affordances
        // are resolved against the order authorization handler below.
        var isPrivilegedReader = RoleChecks.CanAdministerStore(User) || RoleChecks.IsTeamsAdmin(User);
        var pageData = isPrivilegedReader
            ? await storeService.GetAllCounterpartiesIndexDataAsync(ct)
            : await storeService.GetIndexDataAsync(user.Id, ct);
        if (pageData.ShowNoOrdersMessage)
        {
            SetInfo("You don't lead any camps or coordinate any departments this year, so there are no Store orders to manage.");
        }

        var canManage = new Dictionary<Guid, bool>(pageData.Counterparties.Count);
        foreach (var cp in pageData.Counterparties)
        {
            // The order's one actionable affordance on this page: Delete when it
            // exists, Create when it doesn't.
            var (resource, requirement) = cp.Orders.Count > 0
                ? ((object)cp.Orders[0], OrderOperationRequirement.Delete)
                : (new OrderCreateContext(
                       CampSeasonId: cp.CounterpartyType == OrderCounterpartyType.Camp ? cp.CounterpartyId : null,
                       TeamId: cp.CounterpartyType == OrderCounterpartyType.Team ? cp.CounterpartyId : null),
                   OrderOperationRequirement.Create);
            canManage[cp.CounterpartyId] = (await authService.AuthorizeAsync(User, resource, requirement)).Succeeded;
        }

        var model = new IndexViewModel
        {
            Year = pageData.Year,
            Catalog = pageData.Catalog,
            Counterparties = pageData.Counterparties,
            CanManageByCounterparty = canManage
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

        var view = await authService.AuthorizeAsync(User, order, OrderOperationRequirement.View);
        if (!view.Succeeded) return Forbid();

        var canEdit = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.AddLine)).Succeeded;
        var canPay = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.Pay)).Succeeded;
        var canDeleteAuth = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.Delete)).Succeeded;
        var canIssueAuth = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.IssueInvoice)).Succeeded;
        var pageData = await storeService.GetOrderPageDataAsync(order, canEdit, canPay, ct);
        var (catalog, removableLineIds) = await FilterLineEditAffordancesAsync(order, pageData.Catalog, canEdit, ct);
        return View(OrderViewModel.FromPageData(
            pageData,
            canDeleteAuth && order.BalanceEur == 0m && order.State == OrderState.Open,
            canIssueAuth && order.State == OrderState.Open && order.Lines.Count > 0,
            catalog,
            removableLineIds));
    }

    /// <summary>
    /// Per-row line-edit affordances, resolved against <see cref="OrderAuthorizationHandler"/>
    /// (same pattern as the index's per-counterparty gating): the add-line catalog keeps only
    /// products the viewer may still add, and Remove buttons render only for lines the viewer
    /// may still remove — past the product deadline that's Store admins only.
    /// </summary>
    private async Task<(IReadOnlyList<ProductDto> Catalog, IReadOnlyCollection<Guid> RemovableLineIds)>
        FilterLineEditAffordancesAsync(OrderDto order, IReadOnlyList<ProductDto> catalog, bool canEdit, CancellationToken ct)
    {
        if (!canEdit)
            return ([], []);

        var allowedProducts = new List<ProductDto>();
        foreach (var product in catalog)
        {
            var auth = await authService.AuthorizeAsync(
                User, new OrderLineContext(order, product.OrderableUntil), OrderOperationRequirement.AddLine);
            if (auth.Succeeded)
                allowedProducts.Add(product);
        }

        var removable = new List<Guid>();
        var deadlineByProduct = new Dictionary<Guid, LocalDate?>();
        foreach (var line in order.Lines)
        {
            if (!deadlineByProduct.TryGetValue(line.ProductId, out var deadline))
            {
                deadline = (await storeService.GetProductAsync(line.ProductId, ct))?.OrderableUntil;
                deadlineByProduct[line.ProductId] = deadline;
            }
            // Missing product: leave the button visible; the service rejects authoritatively.
            object resource = deadline is { } d ? new OrderLineContext(order, d) : order;
            var auth = await authService.AuthorizeAsync(User, resource, OrderOperationRequirement.RemoveLine);
            if (auth.Succeeded)
                removable.Add(line.Id);
        }

        return (allowedProducts, removable);
    }

    [HttpPost("Order/{id:guid}/Pay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(Guid id, decimal amountEur, CancellationToken ct)
    {
        var (errorResult, _) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, OrderOperationRequirement.Pay);
        if (!auth.Succeeded) return Forbid();

        var orderUrl = Url.Action(nameof(Order), "Store", new { id }, Request.Scheme, Request.Host.Value)
            ?? throw new InvalidOperationException("Failed to compute order URL.");

        try
        {
            // Deliberately not passing the request-scoped token: creating the
            // Checkout Session is a write to Stripe, and the outcome (the
            // session id we redirect to) has to exist whole or not at all
            // (nobodies-collective/Humans#950). The order read above keeps the
            // token — abandoning a read is free.
            var sessionUrl = await storeService.CreateStripeCheckoutSessionAsync(
                order, amountEur, orderUrl, CancellationToken.None);
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

    [HttpPost("Order/{id:guid}/IssueInvoice")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueInvoice(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, OrderOperationRequirement.IssueInvoice);
        if (!auth.Succeeded) return Forbid();

        try
        {
            // No request-scoped token anywhere on this path: issuance creates and approves a
            // document in Holded, and a torn write leaves a doc we have no local record of
            // (memory/architecture/cancellation-token-propagation.md).
            await storeService.IssueInvoiceAsync(id, user.Id, CancellationToken.None);
            SetSuccess("Invoice issued in Holded. The order is now frozen.");
        }
        catch (InvalidOperationException ex)
        {
            // Expected refusals (already invoiced, missing account, receipt over threshold) —
            // the message is written for the admin, so surface it and log at warning.
            logger.LogWarning("Invoice issuance rejected for order {OrderId}: {Reason}", id, ex.Message);
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Holded invoice issuance failed for order {OrderId}", id);
            SetError("Could not issue the invoice in Holded. Check the logs and try again.");
        }
        return RedirectToAction(nameof(Order), new { id });
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
            new OrderCreateContext(CampSeasonId: campSeasonId),
            OrderOperationRequirement.Create);
        if (!auth.Succeeded) return Forbid();

        var newId = await storeService.CreateOrderAsync(campSeasonId, label, user.Id, ct);
        SetSuccess("Order created.");
        return RedirectToAction(nameof(Order), new { id = newId });
    }

    [HttpPost("Team/{teamId:guid}/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTeamOrder(Guid teamId, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var auth = await authService.AuthorizeAsync(
            User,
            new OrderCreateContext(CampSeasonId: null, TeamId: teamId),
            OrderOperationRequirement.Create);
        if (!auth.Succeeded) return Forbid();

        try
        {
            var newId = await storeService.CreateTeamOrderAsync(teamId, user.Id, ct);
            SetSuccess("Team order created.");
            return RedirectToAction(nameof(Order), new { id = newId });
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("Order/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, OrderOperationRequirement.Delete);
        if (!auth.Succeeded) return Forbid();

        try
        {
            await storeService.DeleteOrderAsync(id, user.Id, ct);
            SetSuccess("Order deleted.");
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Order), new { id });
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Order/{id:guid}/AddLine")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLine(Guid id, Guid productId, int qty, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        // Authorize against the product's order deadline when known; an unknown product
        // falls back to the plain order resource and the service rejects it as not found.
        var product = await storeService.GetProductAsync(productId, ct);
        object resource = product is null ? order : new OrderLineContext(order, product.OrderableUntil);
        var auth = await authService.AuthorizeAsync(User, resource, OrderOperationRequirement.AddLine);
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

        // Authorize against the line's product deadline when known; an unknown line/product
        // falls back to the plain order resource and the service rejects it authoritatively.
        var lineProductId = order.Lines.FirstOrDefault(l => l.Id == lineId)?.ProductId;
        var product = lineProductId is { } pid ? await storeService.GetProductAsync(pid, ct) : null;
        object resource = product is null ? order : new OrderLineContext(order, product.OrderableUntil);
        var auth = await authService.AuthorizeAsync(User, resource, OrderOperationRequirement.RemoveLine);
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

        var auth = await authService.AuthorizeAsync(User, order, OrderOperationRequirement.EditCounterparty);
        if (!auth.Succeeded) return Forbid();

        var result = await storeService.UpdateCounterpartyWithResultAsync(id, input, user.Id, ct);
        if (!result.Succeeded)
            SetError(result.ErrorMessage ?? "Could not update counterparty.");
        else
            SetSuccess("Counterparty updated.");

        return RedirectToAction(nameof(Order), new { id });
    }

}
