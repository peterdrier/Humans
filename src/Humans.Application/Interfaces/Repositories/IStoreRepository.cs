using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Materialized view of a <see cref="StoreOrderLine"/> together with the parent
/// order's state and camp season and the product's order deadline. Returned by
/// <see cref="IStoreRepository.GetLineWithOrderAndProductAsync"/>.
/// </summary>
public record StoreLineContext(
    Guid LineId,
    Guid OrderId,
    Guid? CampSeasonId,
    StoreOrderState OrderState,
    LocalDate ProductOrderableUntil);

/// <summary>
/// A recorded Stripe-method <see cref="StorePayment"/> projected for reconciliation
/// against the Stripe Checkout Session list. Returned by
/// <see cref="IStoreRepository.GetRecordedStripePaymentsAsync"/>.
/// </summary>
public record StoreRecordedStripePayment(
    string PaymentIntentId,
    Guid OrderId,
    decimal AmountEur,
    Instant ReceivedAt,
    StorePaymentStatus Status);

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
[Section("Store")]
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
    /// <summary>
    /// Returns every <see cref="StoreOrder"/> whose <c>CampSeasonId</c> is in
    /// <paramref name="campSeasonIds"/>, with <c>Lines</c> and <c>Payments</c>
    /// eager-loaded. Empty input returns an empty list without a round-trip.
    /// </summary>
    Task<IReadOnlyList<StoreOrder>> GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
        IReadOnlyCollection<Guid> campSeasonIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the single open team order for <paramref name="teamId"/> in
    /// <paramref name="year"/>, or null. The "one order per team per year"
    /// invariant is service-enforced; this method returns the first match if
    /// more exist. <c>Lines</c> are eager-loaded.
    /// </summary>
    Task<StoreOrder?> GetOrderForTeamAsync(Guid teamId, int year, CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="StoreOrder"/> whose <c>TeamId</c> is in
    /// <paramref name="teamIds"/> for the given <paramref name="year"/>, with
    /// <c>Lines</c> eager-loaded. Empty input returns an empty list without a
    /// round-trip. Used by the admin summary cross-tab.
    /// </summary>
    Task<IReadOnlyList<StoreOrder>> GetOrdersForTeamsWithLinesAsync(
        IReadOnlyCollection<Guid> teamIds,
        int year,
        CancellationToken ct = default);

    Task AddOrderAsync(StoreOrder order, CancellationToken ct = default);
    Task UpdateOrderAsync(StoreOrder order, CancellationToken ct = default);
    /// <summary>
    /// Hard-deletes the order. Cascade FKs remove its lines and payments. The
    /// service is responsible for enforcing balance/state preconditions.
    /// </summary>
    Task DeleteOrderAsync(Guid orderId, CancellationToken ct = default);

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

    /// <summary>
    /// Returns the single payment recorded against <paramref name="paymentIntentId"/>, or null if
    /// none. Used by the async-payment state machine to read a payment's current
    /// <see cref="StorePayment.Status"/> before transitioning it. The Stripe PI index is unique, so
    /// at most one row matches.
    /// </summary>
    Task<StorePayment?> GetPaymentByStripePaymentIntentIdAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="StorePayment.Status"/> on the payment with the given id. The service owns the
    /// transition rules (which from-states may move to which to-states); this is the bare write.
    /// </summary>
    Task UpdatePaymentStatusAsync(Guid paymentId, StorePaymentStatus status, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes the payment with the given id. Used only by the <c>checkout.session.expired</c>
    /// cleanup of an orphan <see cref="StorePaymentStatus.Pending"/> row; the service enforces the
    /// status precondition before calling.
    /// </summary>
    Task DeletePaymentAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>
    /// Returns every recorded Stripe-method payment (rows with a non-null
    /// <see cref="StorePayment.StripePaymentIntentId"/>), projected for reconciliation.
    /// Feeds both missing-payment detection (Stripe sessions absent here) and orphan
    /// detection (recorded here but absent from Stripe).
    /// </summary>
    Task<IReadOnlyList<StoreRecordedStripePayment>> GetRecordedStripePaymentsAsync(CancellationToken ct = default);

    // Invoices
    Task AddInvoiceAsync(StoreInvoice invoice, CancellationToken ct = default);

    // Treasury sync state
    Task<StoreTreasurySyncState> GetOrCreateTreasurySyncStateAsync(CancellationToken ct = default);
    Task UpdateTreasurySyncStateAsync(StoreTreasurySyncState state, CancellationToken ct = default);
}
