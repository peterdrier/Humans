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

    // ==========================================================================
    // Catalog (write — Phase 3)
    // ==========================================================================

    public Task<Guid> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 3");

    public Task UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 3");

    public Task DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 3");

    // ==========================================================================
    // Orders (read)
    // ==========================================================================

    public async Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        var orders = await _repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
        var products = await _repo.GetActiveProductsForYearAsync(DateTime.UtcNow.Year, ct);
        var productNames = products.ToDictionary(p => p.Id, p => p.Name);
        return orders.Select(o => MapOrder(o, productNames)).ToList();
    }

    public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await _repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (o is null) return null;
        var products = await _repo.GetActiveProductsForYearAsync(DateTime.UtcNow.Year, ct);
        var productNames = products.ToDictionary(p => p.Id, p => p.Name);
        return MapOrder(o, productNames);
    }

    // ==========================================================================
    // Orders (write — Phase 2.4)
    // ==========================================================================

    public Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2.4");

    public Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2.4");

    public Task RemoveLineAsync(Guid lineId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2.4");

    public Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2.4");

    // ==========================================================================
    // Payments / invoices / summary (Phase 5+)
    // ==========================================================================

    public Task RecordManualPaymentAsync(Guid orderId, decimal amountEur, StorePaymentMethod method, string? externalRef, string? notes, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

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
        var lines = o.Lines.Select(l => new OrderLineDto(
            l.Id, l.OrderId, l.ProductId,
            productNames.GetValueOrDefault(l.ProductId, "(unknown product)"),
            l.Qty, l.UnitPriceSnapshot, l.VatRateSnapshot, l.DepositAmountSnapshot, l.AddedAt)).ToList();

        return new OrderDto(
            o.Id, o.CampSeasonId, o.Label, o.State,
            o.CounterpartyName, o.CounterpartyVatId, o.CounterpartyAddress, o.CounterpartyCountryCode, o.CounterpartyEmail,
            o.IssuedInvoiceId,
            lines,
            balance.LinesSubtotalEur, balance.VatTotalEur, balance.DepositTotalEur,
            balance.PaymentsTotalEur, balance.BalanceEur);
    }
}
