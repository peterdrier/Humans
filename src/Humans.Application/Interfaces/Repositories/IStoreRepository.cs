using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Materialized view of a <see cref="StoreOrderLine"/> together with the parent
/// order's state and camp season and the product's order deadline. Returned by
/// <see cref="IStoreRepository.GetLineWithOrderAndProductAsync"/>.
/// </summary>
public record StoreLineContext(
    Guid LineId,
    Guid OrderId,
    Guid CampSeasonId,
    StoreOrderState OrderState,
    LocalDate ProductOrderableUntil);

/// <summary>
/// Repository for the Store section's tables: <c>store_products</c>,
/// <c>store_orders</c>, <c>store_order_lines</c>, <c>store_payments</c>,
/// <c>store_invoices</c>, and <c>store_treasury_sync_state</c>. The only
/// non-test file that writes to these DbSets.
/// </summary>
/// <remarks>
/// Follows the §15b Singleton + <c>IDbContextFactory</c> pattern: every method
/// opens its own short-lived <c>DbContext</c>, performs its work, and saves
/// atomically within that context's lifetime.
/// </remarks>
public interface IStoreRepository : IRepository
{
    // Products
    Task<IReadOnlyList<StoreProduct>> GetActiveProductsForYearAsync(int year, CancellationToken ct = default);
    /// <summary>
    /// Returns all products for the given year regardless of <see cref="StoreProduct.IsActive"/>.
    /// </summary>
    Task<IReadOnlyList<StoreProduct>> GetAllProductsForYearAsync(int year, CancellationToken ct = default);
    Task<StoreProduct?> GetProductByIdAsync(Guid productId, CancellationToken ct = default);
    /// <summary>
    /// Resolves product display names for the given ids regardless of whether
    /// the product is currently active or belongs to the active year. Used by
    /// order-mapping code so issued/historical lines render with their actual
    /// product name even after the product has been deactivated.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetProductNamesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task AddProductAsync(StoreProduct product, CancellationToken ct = default);
    Task UpdateProductAsync(StoreProduct product, CancellationToken ct = default);

    // Orders
    Task<IReadOnlyList<StoreOrder>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default);
    Task<StoreOrder?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default);
    Task<StoreOrder?> GetOrderWithLinesAndPaymentsAsync(Guid orderId, CancellationToken ct = default);
    Task<IReadOnlyList<StoreOrder>> GetAllOrdersAsync(CancellationToken ct = default);
    Task AddOrderAsync(StoreOrder order, CancellationToken ct = default);
    Task UpdateOrderAsync(StoreOrder order, CancellationToken ct = default);

    // Lines
    Task AddLineAsync(StoreOrderLine line, CancellationToken ct = default);
    Task RemoveLineAsync(Guid lineId, CancellationToken ct = default);
    /// <summary>
    /// Returns the line plus its parent order's <see cref="StoreOrder.State"/> and
    /// <see cref="StoreOrder.CampSeasonId"/> and the product's
    /// <see cref="StoreProduct.OrderableUntil"/> deadline. Used by RemoveLineAsync
    /// to enforce the same gate as AddLine without three round trips.
    /// </summary>
    Task<StoreLineContext?> GetLineWithOrderAndProductAsync(Guid lineId, CancellationToken ct = default);

    // Payments
    Task AddPaymentAsync(StorePayment payment, CancellationToken ct = default);
    Task<bool> StripePaymentIntentExistsAsync(string paymentIntentId, CancellationToken ct = default);

    // Invoices
    Task AddInvoiceAsync(StoreInvoice invoice, CancellationToken ct = default);

    // Treasury sync state
    Task<StoreTreasurySyncState> GetOrCreateTreasurySyncStateAsync(CancellationToken ct = default);
    Task UpdateTreasurySyncStateAsync(StoreTreasurySyncState state, CancellationToken ct = default);
}
