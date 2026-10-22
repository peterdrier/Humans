using Humans.Camps.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Domain;
using Humans.Store.Services;
using Humans.Store.Services.Dtos;
using Humans.Base.Authorization;
using Humans.Store.Authorization;
using Humans.Store.Models;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

using Humans.Users.Contracts;

namespace Humans.Store.Controllers;

[Authorize]
[Route("Store")]
internal sealed class StoreController(
    Service storeService,
    ICampService campService,
    IAuthorizationService authService,
    IUserService userService,
    ILogger<StoreController> logger,
    IStringLocalizer<StoreResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        // TeamsAdmins read every counterparty but edit only team orders, so per-row affordances go through the order
        // authorization handler.
        var isPrivilegedReader = RoleChecks.CanAdministerStore(User) || RoleChecks.IsTeamsAdmin(User);
        var pageData = isPrivilegedReader
            ? await storeService.GetAllCounterpartiesIndexDataAsync(ct)
            : await storeService.GetIndexDataAsync(user.Id, ct);

        var canManage = new Dictionary<Guid, bool>(pageData.Counterparties.Count);
        foreach (var cp in pageData.Counterparties)
        {
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
            Catalog = pageData.Catalog.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(),
            Counterparties = pageData.Counterparties,
            CanManageByCounterparty = canManage
        };
        return View(model);
    }

    [HttpGet("Order/{id:guid}")]
    public async Task<IActionResult> Order(Guid id, CancellationToken ct)
    {
        var (errorResult, _) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var view = await authService.AuthorizeAsync(User, order, OrderOperationRequirement.View);
        if (!view.Succeeded) return Forbid();

        var canEdit = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.AddLine)).Succeeded;
        var canPay = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.Pay)).Succeeded;
        var canDeleteAuth = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.Delete)).Succeeded;
        var canIssueAuth = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.IssueInvoice)).Succeeded;
        var canRecordPayment = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.RecordPayment)).Succeeded;
        var canRefund = canRecordPayment
            && (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.Refund)).Succeeded;
        var canDeletePayment = (await authService.AuthorizeAsync(User, order, OrderOperationRequirement.DeletePayment)).Succeeded;
        var pageData = await storeService.GetOrderPageDataAsync(order, canEdit, canPay, ct);
        var (catalog, removableLineIds) = await FilterLineEditAffordancesAsync(order, pageData.Catalog, canEdit, ct);
        return View(OrderViewModel.FromPageData(
            pageData,
            canDeleteAuth && order.IsDeletable,
            canIssueAuth && order.IsInvoiceIssuable,
            canRecordPayment,
            canRefund,
            canDeletePayment,
            catalog.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(),
            removableLineIds));
    }

    /// <summary>Past a product's deadline only Store admins may add or remove its lines.</summary>
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
            // Not the request token: creating the Checkout Session is a Stripe write whose session id must exist
            // whole or not at all.
            var result = await storeService.CreateStripeCheckoutSessionAsync(
                order, amountEur, orderUrl, CancellationToken.None);
            if (result.ErrorKey is { } key)
            {
                SetError(result.MaximumAmount is { } maximumAmount
                    ? localizer[key, maximumAmount].Value : localizer[key].Value);
                return RedirectToAction(nameof(Order), new { id });
            }
            return Redirect(result.SessionUrl!);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe Checkout Session creation failed for order {OrderId}", id);
            SetError(localizer["Store_CheckoutFailed"].Value);
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
            // No request token: issuance creates and approves a Holded document, and a torn write leaves one we have
            // no local record of.
            var result = await storeService.IssueInvoiceAsync(id, user.Id, CancellationToken.None);
            if (result.Succeeded) SetSuccess(localizer["Store_InvoiceIssued"].Value);
            else SetError(result.Refusal ?? localizer["Store_InvoiceFailed"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Holded invoice issuance failed for order {OrderId}", id);
            SetError(localizer["Store_InvoiceFailed"].Value);
        }
        return RedirectToAction(nameof(Order), new { id });
    }

    [HttpPost("Order/{id:guid}/RecordPayment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordPayment(
        Guid id, PaymentMethod method, decimal amountEur, string? externalRef, string? notes, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, OrderOperationRequirement.RecordPayment);
        if (!auth.Succeeded) return Forbid();
        if (method == PaymentMethod.Refund
            && !(await authService.AuthorizeAsync(User, order, OrderOperationRequirement.Refund)).Succeeded)
            return Forbid();

        var result = await storeService.RecordAdminPaymentAsync(id, method, amountEur, externalRef, notes, user.Id, CancellationToken.None);
        if (result.Succeeded) SetSuccess(localizer["Store_PaymentRecorded"].Value);
        else SetError(result.Refusal!);
        return RedirectToAction(nameof(Order), new { id });
    }

    [HttpPost("Order/{id:guid}/Payment/{paymentId:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePayment(Guid id, Guid paymentId, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var order = await storeService.GetOrderAsync(id, ct);
        if (order is null) return NotFound();

        var auth = await authService.AuthorizeAsync(User, order, OrderOperationRequirement.DeletePayment);
        if (!auth.Succeeded) return Forbid();

        var result = await storeService.DeletePaymentAsync(id, paymentId, user.Id, CancellationToken.None);
        if (result.Succeeded) SetSuccess("Payment deleted."); // Admin-only action: exempt from localization.
        else SetError(result.Refusal!);
        return RedirectToAction(nameof(Order), new { id });
    }

    [HttpPost("Order/Create/{campSeasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Guid campSeasonId, CancellationToken ct)
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

        var result = await storeService.CreateOrderAsync(campSeasonId, user.Id, ct);
        if (!result.Succeeded)
        {
            SetError(localizer[result.ErrorKey ?? "Store_CreateOrderFailed"].Value);
            return RedirectToAction(nameof(Index));
        }
        SetSuccess(localizer["Store_OrderCreated"].Value);
        return RedirectToAction(nameof(Order), new { id = result.CreatedId });
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

        var result = await storeService.CreateTeamOrderAsync(teamId, user.Id, ct);
        if (!result.Succeeded)
        {
            SetError(localizer[result.ErrorKey ?? "Store_CreateOrderFailed"].Value);
            return RedirectToAction(nameof(Index));
        }
        SetSuccess(localizer["Store_TeamOrderCreated"].Value);
        return RedirectToAction(nameof(Order), new { id = result.CreatedId });
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

        var result = await storeService.DeleteOrderAsync(id, user.Id, ct);
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            return RedirectToAction(nameof(Order), new { id });
        }
        SetSuccess(localizer["Store_OrderDeleted"].Value);
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

        // Unknown product: fall back to the plain order resource; the service rejects it as not found.
        var product = await storeService.GetProductAsync(productId, ct);
        object resource = product is null ? order : new OrderLineContext(order, product.OrderableUntil);
        var auth = await authService.AuthorizeAsync(User, resource, OrderOperationRequirement.AddLine);
        if (!auth.Succeeded) return Forbid();

        var result = await storeService.AddLineAsync(id, productId, qty, user.Id, ct);
        if (!result.Succeeded)
            SetError(result.ErrorKey is { } key
                ? localizer[key].Value
                : localizer["Store_AddLineFailed"].Value);
        else
            SetSuccess(localizer["Store_LineAdded"].Value);

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

        // Unknown line or product: fall back to the plain order resource; the service rejects it.
        var lineProductId = order.Lines.FirstOrDefault(l => l.Id == lineId)?.ProductId;
        var product = lineProductId is { } pid ? await storeService.GetProductAsync(pid, ct) : null;
        object resource = product is null ? order : new OrderLineContext(order, product.OrderableUntil);
        var auth = await authService.AuthorizeAsync(User, resource, OrderOperationRequirement.RemoveLine);
        if (!auth.Succeeded) return Forbid();

        var result = await storeService.RemoveLineAsync(id, lineId, user.Id, ct);
        if (!result.Succeeded)
            SetError(localizer[result.ErrorKey ?? "Store_RemoveLineFailed"].Value);
        else
            SetSuccess(localizer["Store_LineRemoved"].Value);

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

        var result = await storeService.UpdateCounterpartyAsync(id, input, user.Id, ct);
        if (!result.Succeeded)
            SetError(localizer[result.ErrorKey ?? "Store_UpdateCounterpartyFailed"].Value);
        else
            SetSuccess(localizer["Store_CounterpartyUpdated"].Value);

        return RedirectToAction(nameof(Order), new { id });
    }

}
