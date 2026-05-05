using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Store;

public class StoreService : IStoreService
{
    private readonly IStoreRepository _repo;
    private readonly IAuditLogService _audit;
    private readonly IClock _clock;
    private readonly IShiftManagementService _shifts;

    public StoreService(
        IStoreRepository repo,
        IAuditLogService audit,
        IClock clock,
        IShiftManagementService shifts)
    {
        _repo = repo;
        _audit = audit;
        _clock = clock;
        _shifts = shifts;
    }

    // ==========================================================================
    // Catalog (read)
    // ==========================================================================

    public async Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default)
    {
        var products = await _repo.GetActiveProductsForYearAsync(year, ct);
        return products.Select(MapProduct).ToList();
    }

    public async Task<IReadOnlyList<ProductDto>> GetAllProductsForYearAsync(int year, CancellationToken ct = default)
    {
        var products = await _repo.GetAllProductsForYearAsync(year, ct);
        return products.Select(MapProduct).ToList();
    }

    public async Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        var p = await _repo.GetProductByIdAsync(productId, ct);
        return p is null ? null : MapProduct(p);
    }

    // ==========================================================================
    // Catalog (write — Phase 3)
    // ==========================================================================

    public async Task<Guid> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateProductDraft(draft);

        var now = _clock.GetCurrentInstant();
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
        await _repo.AddProductAsync(product, ct);
        await _audit.LogAsync(
            AuditAction.StoreProductCreated, nameof(StoreProduct), product.Id,
            $"Created store product '{product.Name}' for year {product.Year}",
            actorUserId);
        return product.Id;
    }

    public async Task UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateProductDraft(draft);

        var product = await _repo.GetProductByIdAsync(draft.Id, ct)
            ?? throw new InvalidOperationException($"Product {draft.Id} not found");

        product.Year = draft.Year;
        product.Name = draft.Name.Trim();
        product.Description = draft.Description;
        product.UnitPriceEur = draft.UnitPriceEur;
        product.VatRatePercent = draft.VatRatePercent;
        product.DepositAmountEur = draft.DepositAmountEur;
        product.OrderableUntil = draft.OrderableUntil;
        product.IsActive = draft.IsActive;
        product.UpdatedAt = _clock.GetCurrentInstant();

        await _repo.UpdateProductAsync(product, ct);
        await _audit.LogAsync(
            AuditAction.StoreProductUpdated, nameof(StoreProduct), product.Id,
            $"Updated store product '{product.Name}'",
            actorUserId);
    }

    public async Task DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default)
    {
        var product = await _repo.GetProductByIdAsync(productId, ct)
            ?? throw new InvalidOperationException($"Product {productId} not found");

        product.IsActive = false;
        product.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.UpdateProductAsync(product, ct);

        await _audit.LogAsync(
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

    // ==========================================================================
    // Orders (read)
    // ==========================================================================

    public async Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        var orders = await _repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
        var productIds = orders.SelectMany(o => o.Lines).Select(l => l.ProductId).Distinct().ToList();
        var productNames = await _repo.GetProductNamesByIdsAsync(productIds, ct);
        return orders.Select(o => MapOrder(o, productNames)).ToList();
    }

    public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await _repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (o is null) return null;
        var productIds = o.Lines.Select(l => l.ProductId).Distinct().ToList();
        var productNames = await _repo.GetProductNamesByIdsAsync(productIds, ct);
        return MapOrder(o, productNames);
    }

    // ==========================================================================
    // Orders (write)
    // ==========================================================================

    public async Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var order = new StoreOrder
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            Label = label,
            State = StoreOrderState.Open,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _repo.AddOrderAsync(order, ct);
        await _audit.LogAsync(
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

        var order = await _repo.GetOrderByIdAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found");

        if (order.State != StoreOrderState.Open)
            throw new InvalidOperationException("Cannot add lines to an issued order");

        var product = await _repo.GetProductByIdAsync(productId, ct)
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
            AddedAt = _clock.GetCurrentInstant(),
            AddedByUserId = actorUserId
        };
        await _repo.AddLineAsync(line, ct);
        await _audit.LogAsync(
            AuditAction.StoreLineAdded, nameof(StoreOrderLine), line.Id,
            $"Added {qty} × '{product.Name}' to order {order.Id}",
            actorUserId, order.Id, nameof(StoreOrder));
    }

    public async Task RemoveLineAsync(Guid orderId, Guid lineId, Guid actorUserId, CancellationToken ct = default)
    {
        var ctx = await _repo.GetLineWithOrderAndProductAsync(lineId, ct)
            ?? throw new InvalidOperationException($"Line {lineId} not found");

        if (ctx.OrderId != orderId)
            throw new InvalidOperationException($"Line {lineId} does not belong to order {orderId}");

        if (ctx.OrderState != StoreOrderState.Open)
            throw new InvalidOperationException("Cannot remove lines from an issued order");

        var today = await TodayInEventZoneAsync();
        if (today > ctx.ProductOrderableUntil)
            throw new InvalidOperationException(
                $"Line's product order deadline ({ctx.ProductOrderableUntil}) has passed");

        await _repo.RemoveLineAsync(lineId, ct);
        await _audit.LogAsync(
            AuditAction.StoreLineRemoved, nameof(StoreOrderLine), lineId,
            $"Removed line {lineId} from order {ctx.OrderId}",
            actorUserId, ctx.OrderId, nameof(StoreOrder));
    }

    public async Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await _repo.GetOrderByIdAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found");

        order.CounterpartyName = input.Name;
        order.CounterpartyVatId = input.VatId;
        order.CounterpartyAddress = input.Address;
        order.CounterpartyCountryCode = input.CountryCode;
        order.CounterpartyEmail = input.Email;
        order.UpdatedAt = _clock.GetCurrentInstant();

        await _repo.UpdateOrderAsync(order, ct);
        await _audit.LogAsync(
            AuditAction.StoreCounterpartyEdited, nameof(StoreOrder), orderId,
            $"Updated counterparty on order {orderId}",
            actorUserId);
    }

    private async Task<LocalDate> TodayInEventZoneAsync()
    {
        var activeEvent = await _shifts.GetActiveAsync();
        var tz = activeEvent is null
            ? DateTimeZone.Utc
            : DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        return _clock.GetCurrentInstant().InZone(tz).Date;
    }

    // ==========================================================================
    // Payments / invoices / summary (Phase 5+)
    // ==========================================================================

    public Task RecordManualPaymentAsync(Guid orderId, decimal amountEur, StorePaymentMethod method, string? externalRef, string? notes, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public async Task RecordStripePaymentAsync(Guid orderId, string paymentIntentId, decimal amountEur, CancellationToken ct = default)
    {
        if (amountEur <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountEur), "Stripe payment amount must be positive.");
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            throw new ArgumentException("PaymentIntent id is required.", nameof(paymentIntentId));

        if (await _repo.StripePaymentIntentExistsAsync(paymentIntentId, ct))
            return; // idempotent: duplicate Stripe webhook delivery

        var payment = new StorePayment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = amountEur,
            Method = StorePaymentMethod.Stripe,
            StripePaymentIntentId = paymentIntentId,
            ReceivedAt = _clock.GetCurrentInstant(),
            RecordedByUserId = null,
        };
        await _repo.AddPaymentAsync(payment, ct);
        await _audit.LogAsync(
            AuditAction.StorePaymentRecorded, nameof(StorePayment), payment.Id,
            $"Recorded Stripe payment of EUR {amountEur:0.00} on order {orderId} (PI {paymentIntentId})",
            "StripeWebhook",
            orderId, nameof(StoreOrder));
    }

    public Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public Task<IReadOnlyList<OrderSummaryDto>> GetAllOrderSummariesAsync(int year, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    // ==========================================================================
    // Mapping
    // ==========================================================================

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
