using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;

namespace Humans.Application.Services.Store;

public class StoreService(
    IStoreRepository repo,
    IAuditLogService audit,
    ICampService campService,
    IClock clock,
    IShiftManagementService shifts,
    IStripeService stripeService,
    ILogger<StoreService> logger) : IStoreService
{
    public async Task<StoreIndexData> GetIndexDataAsync(
        Guid userId,
        bool isPrivilegedReader,
        CancellationToken ct = default)
    {
        var activeEvent = await shifts.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var catalog = (await GetActiveCatalogAsync(year, ct))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var sections = new List<StoreCampSeasonOrders>();
        var leadSeasonId = await campService.GetCampLeadSeasonIdForYearAsync(userId, year, ct);
        if (leadSeasonId is { } seasonId)
        {
            var season = await campService.GetCampSeasonByIdAsync(seasonId, ct);
            if (season is not null)
            {
                var orders = await GetOrdersForCampSeasonAsync(season.Id, ct);
                sections.Add(new StoreCampSeasonOrders(season.Id, season.Name, year, orders));
            }
        }

        return new StoreIndexData(
            year,
            catalog,
            sections,
            sections.Count == 0 && !isPrivilegedReader);
    }

    public async Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default)
    {
        var products = await repo.GetActiveProductsForYearAsync(year, ct);
        return products
            .Select(MapProduct)
            .ToList();
    }

    public async Task<StoreOrderPageData> GetOrderPageDataAsync(
        OrderDto order,
        bool canEdit,
        bool canPayAuthorized,
        CancellationToken ct = default)
    {
        IReadOnlyList<ProductDto> catalog = [];
        if (canEdit)
        {
            var activeEvent = await shifts.GetActiveAsync();
            var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
            catalog = (await GetActiveCatalogAsync(year, ct))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
        }

        var season = await campService.GetCampSeasonByIdAsync(order.CampSeasonId, ct);

        return new StoreOrderPageData(
            order,
            catalog,
            season?.Name ?? "(unknown camp)",
            canEdit,
            canPayAuthorized && order.BalanceEur > 0,
            stripeService.IsStoreCheckoutConfigured);
    }

    public async Task<IReadOnlyList<ProductDto>> GetAllProductsForYearAsync(int year, CancellationToken ct = default)
    {
        var products = await repo.GetAllProductsForYearAsync(year, ct);
        return products
            .Select(MapProduct)
            .ToList();
    }

    public async Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        var p = await repo.GetProductByIdAsync(productId, ct);
        return p is null ? null : MapProduct(p);
    }

    public async Task<Guid> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateProductDraft(draft);

        var now = clock.GetCurrentInstant();
        var product = new StoreProduct
        {
            Id = Guid.NewGuid(),
            Year = draft.Year,
            Name = draft.Name.Trim(),
            Description = draft.Description,
            UnitPriceEur = draft.UnitPriceEur,
            VatRatePercent = draft.VatRatePercent,
            DepositAmountEur = draft.DepositAmountEur,
            OrderableUntil = draft.OrderableUntil,
            IsActive = draft.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddProductAsync(product, ct);
        await audit.LogAsync(
            AuditAction.StoreProductCreated, nameof(StoreProduct), product.Id,
            $"Created store product '{product.Name}' for year {product.Year}",
            actorUserId);
        return product.Id;
    }

    public async Task UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateProductDraft(draft);

        var product = await repo.GetProductByIdAsync(draft.Id, ct)
            ?? throw new InvalidOperationException($"Product {draft.Id} not found");

        product.Year = draft.Year;
        product.Name = draft.Name.Trim();
        product.Description = draft.Description;
        product.UnitPriceEur = draft.UnitPriceEur;
        product.VatRatePercent = draft.VatRatePercent;
        product.DepositAmountEur = draft.DepositAmountEur;
        product.OrderableUntil = draft.OrderableUntil;
        product.IsActive = draft.IsActive;
        product.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateProductAsync(product, ct);
        await audit.LogAsync(
            AuditAction.StoreProductUpdated, nameof(StoreProduct), product.Id,
            $"Updated store product '{product.Name}'",
            actorUserId);
    }

    public async Task<StoreCatalogSaveResult> SaveProductWithResultAsync(
        StoreProductSaveRequest request,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var parseResult = LocalDatePattern.Iso.Parse(request.OrderableUntil ?? string.Empty);
        if (!parseResult.Success)
            return StoreCatalogSaveResult.Failure(nameof(request.OrderableUntil), "Invalid date - use YYYY-MM-DD.");

        var dto = new ProductDto(
            request.Id ?? Guid.Empty,
            request.Year,
            request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            request.UnitPriceEur,
            request.VatRatePercent,
            request.DepositAmountEur,
            parseResult.Value,
            request.IsActive);

        try
        {
            if (request.Id is null)
            {
                await CreateProductAsync(dto, actorUserId, ct);
                return StoreCatalogSaveResult.Success(created: true);
            }

            await UpdateProductAsync(dto, actorUserId, ct);
            return StoreCatalogSaveResult.Success(created: false);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Store catalog Save validation failed: {Reason}", ex.Message);
            return StoreCatalogSaveResult.Failure(null, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Store catalog Save rejected: {Reason}", ex.Message);
            return StoreCatalogSaveResult.Failure(null, ex.Message);
        }
    }

    public async Task DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default)
    {
        var product = await repo.GetProductByIdAsync(productId, ct)
            ?? throw new InvalidOperationException($"Product {productId} not found");

        product.IsActive = false;
        product.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateProductAsync(product, ct);

        await audit.LogAsync(
            AuditAction.StoreProductDeactivated, nameof(StoreProduct), productId,
            $"Deactivated store product '{product.Name}'",
            actorUserId);
    }

    private static void ValidateProductDraft(ProductDto draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new ArgumentException("Product name is required", nameof(draft));
        if (draft.UnitPriceEur < 0m)
            throw new ArgumentException("Unit price cannot be negative", nameof(draft));
        if (draft.VatRatePercent < 0m)
            throw new ArgumentException("VAT rate cannot be negative", nameof(draft));
        if (draft.DepositAmountEur is < 0m)
            throw new ArgumentException("Deposit cannot be negative", nameof(draft));
    }

    public async Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        var orders = await repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
        var productIds = orders.SelectMany(o => o.Lines).Select(l => l.ProductId).Distinct().ToList();
        var productNames = await repo.GetProductNamesByIdsAsync(productIds, ct);
        return orders.Select(o => MapOrder(o, productNames)).ToList();
    }

    public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (o is null) return null;
        var productIds = o.Lines.Select(l => l.ProductId).Distinct().ToList();
        var productNames = await repo.GetProductNamesByIdsAsync(productIds, ct);
        return MapOrder(o, productNames);
    }

    public async Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var order = new StoreOrder
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            Label = label,
            State = StoreOrderState.Open,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddOrderAsync(order, ct);
        await audit.LogAsync(
            AuditAction.StoreOrderCreated, nameof(StoreOrder), order.Id,
            $"Created store order for camp season {campSeasonId}" +
            (string.IsNullOrWhiteSpace(label) ? string.Empty : $" — '{label}'"),
            actorUserId);
        return order.Id;
    }

    public async Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default)
    {
        if (qty <= 0)
            throw new ArgumentException("Qty must be positive", nameof(qty));

        var order = await repo.GetOrderByIdAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found");

        if (order.State != StoreOrderState.Open)
            throw new InvalidOperationException("Cannot add lines to an issued order");

        var product = await repo.GetProductByIdAsync(productId, ct)
            ?? throw new InvalidOperationException($"Product {productId} not found");

        if (!product.IsActive)
            throw new InvalidOperationException(
                $"Product '{product.Name}' has been deactivated and is no longer orderable");

        var today = await TodayInEventZoneAsync();
        if (today > product.OrderableUntil)
            throw new InvalidOperationException(
                $"Product '{product.Name}' order deadline ({product.OrderableUntil}) has passed");

        var line = new StoreOrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            ProductId = product.Id,
            Qty = qty,
            UnitPriceSnapshot = product.UnitPriceEur,
            VatRateSnapshot = product.VatRatePercent,
            DepositAmountSnapshot = product.DepositAmountEur,
            AddedAt = clock.GetCurrentInstant(),
            AddedByUserId = actorUserId
        };
        await repo.AddLineAsync(line, ct);
        await audit.LogAsync(
            AuditAction.StoreLineAdded, nameof(StoreOrderLine), line.Id,
            $"Added {qty} × '{product.Name}' to order {order.Id}",
            actorUserId, order.Id, nameof(StoreOrder));
    }

    public async Task<StoreMutationResult> AddLineWithResultAsync(
        Guid orderId,
        Guid productId,
        int qty,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await AddLineAsync(orderId, productId, qty, actorUserId, ct);
            return StoreMutationResult.Success;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("AddLine validation failed for order {OrderId}: {Reason}", orderId, ex.Message);
            return StoreMutationResult.Failure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("AddLine rejected for order {OrderId}: {Reason}", orderId, ex.Message);
            return StoreMutationResult.Failure(ex.Message);
        }
    }

    public async Task RemoveLineAsync(Guid orderId, Guid lineId, Guid actorUserId, CancellationToken ct = default)
    {
        var ctx = await repo.GetLineWithOrderAndProductAsync(lineId, ct)
            ?? throw new InvalidOperationException($"Line {lineId} not found");

        if (ctx.OrderId != orderId)
            throw new InvalidOperationException($"Line {lineId} does not belong to order {orderId}");

        if (ctx.OrderState != StoreOrderState.Open)
            throw new InvalidOperationException("Cannot remove lines from an issued order");

        var today = await TodayInEventZoneAsync();
        if (today > ctx.ProductOrderableUntil)
            throw new InvalidOperationException(
                $"Line's product order deadline ({ctx.ProductOrderableUntil}) has passed");

        await repo.RemoveLineAsync(lineId, ct);
        await audit.LogAsync(
            AuditAction.StoreLineRemoved, nameof(StoreOrderLine), lineId,
            $"Removed line {lineId} from order {ctx.OrderId}",
            actorUserId, ctx.OrderId, nameof(StoreOrder));
    }

    public async Task<StoreMutationResult> RemoveLineWithResultAsync(
        Guid orderId,
        Guid lineId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await RemoveLineAsync(orderId, lineId, actorUserId, ct);
            return StoreMutationResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("RemoveLine rejected for line {LineId}: {Reason}", lineId, ex.Message);
            return StoreMutationResult.Failure(ex.Message);
        }
    }

    public async Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderByIdAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found");

        order.CounterpartyName = input.Name;
        order.CounterpartyVatId = input.VatId;
        order.CounterpartyAddress = input.Address;
        order.CounterpartyCountryCode = input.CountryCode;
        order.CounterpartyEmail = input.Email;
        order.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateOrderAsync(order, ct);
        await audit.LogAsync(
            AuditAction.StoreCounterpartyEdited, nameof(StoreOrder), orderId,
            $"Updated counterparty on order {orderId}",
            actorUserId);
    }

    public async Task<StoreMutationResult> UpdateCounterpartyWithResultAsync(
        Guid orderId,
        OrderCounterpartyInput input,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await UpdateCounterpartyAsync(orderId, input, actorUserId, ct);
            return StoreMutationResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("UpdateCounterparty rejected for order {OrderId}: {Reason}", orderId, ex.Message);
            return StoreMutationResult.Failure(ex.Message);
        }
    }

    private async Task<LocalDate> TodayInEventZoneAsync()
    {
        var activeEvent = await shifts.GetActiveAsync();
        var tz = activeEvent is null
            ? DateTimeZone.Utc
            : DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        return clock.GetCurrentInstant().InZone(tz).Date;
    }

    public Task RecordManualPaymentAsync(Guid orderId, decimal amountEur, StorePaymentMethod method, string? externalRef, string? notes, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public Task<string> CreateStripeCheckoutSessionAsync(
        OrderDto order,
        decimal amountEur,
        string returnUrl,
        CancellationToken ct = default)
    {
        if (!stripeService.IsStoreCheckoutConfigured)
            throw new InvalidOperationException("Stripe is not configured for this environment. Contact an admin.");

        if (amountEur <= 0)
            throw new InvalidOperationException("Payment amount must be greater than zero.");

        if (amountEur > order.BalanceEur)
            throw new InvalidOperationException($"Payment amount cannot exceed the outstanding balance (EUR {order.BalanceEur:0.00}).");

        var description = $"Nobodies Collective - {order.CounterpartyName ?? "Camp order"}"
            + (string.IsNullOrWhiteSpace(order.Label) ? string.Empty : $" ({order.Label})");

        return stripeService.CreateCheckoutSessionAsync(
            storeOrderId: order.Id,
            amountEur: amountEur,
            successUrl: returnUrl,
            cancelUrl: returnUrl,
            customerEmail: order.CounterpartyEmail,
            lineItemDescription: description,
            ct: ct);
    }

    public async Task RecordStripePaymentAsync(Guid orderId, string paymentIntentId, decimal amountEur, CancellationToken ct = default)
    {
        if (amountEur <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountEur), "Stripe payment amount must be positive.");
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            throw new ArgumentException("PaymentIntent id is required.", nameof(paymentIntentId));

        if (await repo.StripePaymentIntentExistsAsync(paymentIntentId, ct))
            return; // idempotent: duplicate Stripe webhook delivery

        var payment = new StorePayment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = amountEur,
            Method = StorePaymentMethod.Stripe,
            StripePaymentIntentId = paymentIntentId,
            ReceivedAt = clock.GetCurrentInstant(),
            RecordedByUserId = null,
        };
        await repo.AddPaymentAsync(payment, ct);
        await audit.LogAsync(
            AuditAction.StorePaymentRecorded, nameof(StorePayment), payment.Id,
            $"Recorded Stripe payment of EUR {amountEur:0.00} on order {orderId} (PI {paymentIntentId})",
            "StripeWebhook",
            orderId, nameof(StoreOrder));
    }

    public async Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default)
    {
        switch (evt.Kind)
        {
            case StoreCheckoutEventKind.CheckoutSessionCompleted:
                await HandleCheckoutSessionCompletedAsync(evt, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded:
            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed:
            case StoreCheckoutEventKind.CheckoutSessionExpired:
                logger.LogWarning(
                    "Stripe webhook event {Kind} (id={EventId}) received but not yet handled - async-payment state machine pending (nobodies-collective/Humans#638).",
                    evt.Kind, evt.EventId);
                break;

            default:
                logger.LogDebug("Ignoring Stripe webhook event {EventId} of unhandled kind {Kind}", evt.EventId, evt.Kind);
                break;
        }
    }

    private async Task HandleCheckoutSessionCompletedAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct)
    {
        if (evt.Session is not { } session)
        {
            logger.LogWarning("checkout.session.completed event {EventId} did not contain a Session payload", evt.EventId);
            return;
        }

        if (session.OrderId is not { } orderId)
        {
            logger.LogWarning(
                "Stripe Checkout Session {SessionId} has no humans_store_order_id metadata; skipping.",
                session.SessionId);
            return;
        }

        if (session.PaymentIntentId is not { } paymentIntentId)
        {
            logger.LogWarning(
                "Stripe Checkout Session {SessionId} has no PaymentIntentId; skipping.",
                session.SessionId);
            return;
        }

        if (session.AmountEur is not { } amountEur || amountEur <= 0)
        {
            logger.LogWarning(
                "Stripe Checkout Session {SessionId} has non-positive AmountTotal; skipping.",
                session.SessionId);
            return;
        }

        try
        {
            await RecordStripePaymentAsync(orderId, paymentIntentId, amountEur, ct);
            logger.LogInformation(
                "Recorded Stripe payment for order {OrderId} (session {SessionId}, PI {PaymentIntentId}, EUR {Amount})",
                orderId, session.SessionId, paymentIntentId, amountEur);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to record Stripe payment for order {OrderId} (session {SessionId})",
                orderId, session.SessionId);
        }
    }

    public Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public async Task<StoreSummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default)
    {
        var seasonsForYear = await _campService.GetCampSeasonDisplayDataForYearAsync(year, ct);
        var products = await _repo.GetAllProductsForYearAsync(year, ct);

        var orders = seasonsForYear.Count == 0
            ? (IReadOnlyList<StoreOrder>)Array.Empty<StoreOrder>()
            : await _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                seasonsForYear.Keys.ToList(), ct);

        var ordersInYear = orders
            .Where(o => seasonsForYear.ContainsKey(o.CampSeasonId))
            .ToList();

        var productNames = products.ToDictionary(p => p.Id, p => p.Name);

        var byCamp = ordersInYear
            .Select(o =>
            {
                var totals = BalanceCalculator.Compute(o);
                var totalDue = totals.LinesSubtotalEur + totals.VatTotalEur + totals.DepositTotalEur;
                var campName = seasonsForYear[o.CampSeasonId].Name;
                return new OrderSummaryDto(
                    o.Id,
                    o.CampSeasonId,
                    campName,
                    o.Label,
                    o.State,
                    totalDue,
                    totals.PaymentsTotalEur,
                    totals.BalanceEur);
            })
            .OrderBy(s => s.CampName, StringComparer.Ordinal)
            .ToList();

        var byItem = ordersInYear
            .SelectMany(o => o.Lines.Select(l => new
            {
                l.ProductId,
                l.Qty,
                Subtotal = l.Qty * l.UnitPriceSnapshot,
                Vat = Math.Round(l.Qty * l.UnitPriceSnapshot * l.VatRateSnapshot / 100m, 2, MidpointRounding.AwayFromZero),
                Deposit = l.DepositAmountSnapshot is { } d ? l.Qty * d : 0m
            }))
            .GroupBy(x => x.ProductId)
            .Select(g => new ProductAggregateDto(
                g.Key,
                productNames.TryGetValue(g.Key, out var n) ? n : "(unknown)",
                g.Sum(x => x.Qty),
                g.Sum(x => x.Subtotal + x.Vat + x.Deposit)))
            .OrderByDescending(p => p.TotalQty)
            .ThenBy(p => p.ProductName, StringComparer.Ordinal)
            .ToList();

        var productColumns = byItem
            .Select(p => new StoreCrossTabColumn(p.ProductId, p.ProductName, p.TotalQty))
            .OrderBy(c => c.ProductName, StringComparer.Ordinal)
            .ToList();

        var campRows = ordersInYear
            .GroupBy(o => o.CampSeasonId)
            .Select(g =>
            {
                var perProduct = g
                    .SelectMany(o => o.Lines)
                    .GroupBy(l => l.ProductId)
                    .ToDictionary(lg => lg.Key, lg => lg.Sum(l => l.Qty));
                var total = perProduct.Values.Sum();
                return new StoreCrossTabRow(
                    g.Key,
                    seasonsForYear[g.Key].Name,
                    total,
                    perProduct);
            })
            .OrderBy(r => r.CampName, StringComparer.Ordinal)
            .ToList();

        return new StoreSummaryDto(
            year,
            byCamp,
            byItem,
            new StoreCrossTabDto(productColumns, campRows));
    }

    private static ProductDto MapProduct(StoreProduct p) =>
        new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
            p.DepositAmountEur, p.OrderableUntil, p.IsActive);

    private static OrderDto MapOrder(StoreOrder o, IReadOnlyDictionary<Guid, string> productNames)
    {
        var balance = BalanceCalculator.Compute(o);
        var totalsByLine = balance.Lines.ToDictionary(t => t.LineId);
        var lines = o.Lines.Select(l =>
        {
            var t = totalsByLine[l.Id];
            return new OrderLineDto(
                l.Id, l.OrderId, l.ProductId,
                productNames.GetValueOrDefault(l.ProductId, "(unknown product)"),
                l.Qty, l.UnitPriceSnapshot, l.VatRateSnapshot, l.DepositAmountSnapshot, l.AddedAt,
                t.SubtotalEur, t.VatEur, t.DepositEur, t.TotalEur);
        }).ToList();

        return new OrderDto(
            o.Id, o.CampSeasonId, o.Label, o.State,
            o.CounterpartyName, o.CounterpartyVatId, o.CounterpartyAddress, o.CounterpartyCountryCode, o.CounterpartyEmail,
            o.IssuedInvoiceId,
            lines,
            balance.LinesSubtotalEur, balance.VatTotalEur, balance.DepositTotalEur,
            balance.PaymentsTotalEur, balance.BalanceEur);
    }
}
