using Humans.Base.Interfaces.Repositories;
using Humans.Store.Domain;

namespace Humans.Store.Data;

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
/// <remarks>
/// Keeps its prefix where the rest of the section's internals drop theirs
/// (nobodies-collective/Humans#866 design §6a): it derives from
/// <see cref="IRepository"/>, so it cannot itself be called <c>IRepository</c> —
/// the same kind of mechanical reason the controllers and <c>StoreDbContext</c> have.
/// </remarks>
internal interface IStoreRepository : IRepository
{
    // Products
    Task<IReadOnlyList<Product>> GetActiveProductsForYearAsync(int year, CancellationToken ct = default);
    /// <summary>
    /// Returns all products for the given year regardless of <see cref="Product.IsActive"/>.
    /// </summary>
    Task<IReadOnlyList<Product>> GetAllProductsForYearAsync(int year, CancellationToken ct = default);
    Task<Product?> GetProductByIdAsync(Guid productId, CancellationToken ct = default);
    /// <summary>
    /// Resolves product display names for the given ids regardless of whether
    /// the product is currently active or belongs to the active year. Used by
    /// order-mapping code so issued/historical lines render with their actual
    /// product name even after the product has been deactivated.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetProductNamesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task AddProductAsync(Product product, CancellationToken ct = default);
    Task UpdateProductAsync(Product product, CancellationToken ct = default);

    // Orders
    Task<IReadOnlyList<Order>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default);
    Task<Order?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default);
    Task<Order?> GetOrderWithLinesAndPaymentsAsync(Guid orderId, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetAllOrdersAsync(CancellationToken ct = default);
    /// <summary>
    /// Returns every <see cref="Order"/> whose <c>CampSeasonId</c> is in
    /// <paramref name="campSeasonIds"/>, with <c>Lines</c> and <c>Payments</c>
    /// eager-loaded. Empty input returns an empty list without a round-trip.
    /// </summary>
    Task<IReadOnlyList<Order>> GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
        IReadOnlyCollection<Guid> campSeasonIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the single open team order for <paramref name="teamId"/> in
    /// <paramref name="year"/>, or null. The "one order per team per year"
    /// invariant is service-enforced; this method returns the first match if
    /// more exist. <c>Lines</c> are eager-loaded.
    /// </summary>
    Task<Order?> GetOrderForTeamAsync(Guid teamId, int year, CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="Order"/> whose <c>TeamId</c> is in
    /// <paramref name="teamIds"/> for the given <paramref name="year"/>, with
    /// <c>Lines</c> eager-loaded. Empty input returns an empty list without a
    /// round-trip. Used by the admin summary cross-tab.
    /// </summary>
    Task<IReadOnlyList<Order>> GetOrdersForTeamsWithLinesAsync(
        IReadOnlyCollection<Guid> teamIds,
        int year,
        CancellationToken ct = default);

    Task AddOrderAsync(Order order, CancellationToken ct = default);
    Task UpdateOrderAsync(Order order, CancellationToken ct = default);
    /// <summary>
    /// Hard-deletes the order. Cascade FKs remove its lines and payments. The
    /// service is responsible for enforcing balance/state preconditions.
    /// </summary>
    Task DeleteOrderAsync(Guid orderId, CancellationToken ct = default);

    // Lines
    Task AddLineAsync(OrderLine line, CancellationToken ct = default);
    Task RemoveLineAsync(Guid lineId, CancellationToken ct = default);
    /// <summary>
    /// Returns the line plus its parent order's <see cref="Order.State"/> and
    /// <see cref="Order.CampSeasonId"/> and the product's
    /// <see cref="Product.OrderableUntil"/> deadline. Used by RemoveLineAsync
    /// to enforce the same gate as AddLine without three round trips.
    /// </summary>
    Task<LineContext?> GetLineWithOrderAndProductAsync(Guid lineId, CancellationToken ct = default);

    // Payments
    Task AddPaymentAsync(Payment payment, CancellationToken ct = default);
    Task<bool> StripePaymentIntentExistsAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>
    /// Returns the single payment recorded against <paramref name="paymentIntentId"/>, or null if
    /// none. Used by the async-payment state machine to read a payment's current
    /// <see cref="Payment.Status"/> before transitioning it. The Stripe PI index is unique, so
    /// at most one row matches.
    /// </summary>
    Task<Payment?> GetPaymentByStripePaymentIntentIdAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="Payment.Status"/> on the payment with the given id. The service owns the
    /// transition rules (which from-states may move to which to-states); this is the bare write.
    /// </summary>
    Task UpdatePaymentStatusAsync(Guid paymentId, PaymentStatus status, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes the payment with the given id. Used only by the <c>checkout.session.expired</c>
    /// cleanup of an orphan <see cref="PaymentStatus.Pending"/> row; the service enforces the
    /// status precondition before calling.
    /// </summary>
    Task DeletePaymentAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>
    /// Returns every recorded Stripe-method payment (rows with a non-null
    /// <see cref="Payment.StripePaymentIntentId"/>), projected for reconciliation.
    /// Feeds both missing-payment detection (Stripe sessions absent here) and orphan
    /// detection (recorded here but absent from Stripe).
    /// </summary>
    Task<IReadOnlyList<RecordedStripePayment>> GetRecordedStripePaymentsAsync(CancellationToken ct = default);

    // Invoices
    Task AddInvoiceAsync(Invoice invoice, CancellationToken ct = default);

    // Treasury sync state
    Task<TreasurySyncState> GetOrCreateTreasurySyncStateAsync(CancellationToken ct = default);
    Task UpdateTreasurySyncStateAsync(TreasurySyncState state, CancellationToken ct = default);
}
