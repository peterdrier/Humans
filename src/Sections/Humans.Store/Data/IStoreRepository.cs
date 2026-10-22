using Humans.Base.Interfaces.Repositories;
using Humans.Store.Domain;

namespace Humans.Store.Data;

internal interface IStoreRepository : IRepository
{
    Task<IReadOnlyList<Product>> GetActiveProductsForYearAsync(int year, CancellationToken ct = default);
    /// <summary>Includes inactive products.</summary>
    Task<IReadOnlyList<Product>> GetAllProductsForYearAsync(int year, CancellationToken ct = default);
    Task<Product?> GetProductByIdAsync(Guid productId, CancellationToken ct = default);
    /// <summary>
    /// Includes inactive and other-year products, so historical lines keep their real product name.
    /// </summary>
    Task<IReadOnlyList<Product>> GetProductsByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task AddProductAsync(Product product, CancellationToken ct = default);
    Task UpdateProductAsync(Product product, CancellationToken ct = default);

    Task<IReadOnlyList<Order>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default);
    Task<Order?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default);
    Task<Order?> GetOrderWithLinesAndPaymentsAsync(Guid orderId, CancellationToken ct = default);
    /// <summary>Legacy camp orders whose persisted year is unresolved.</summary>
    Task<IReadOnlyList<Order>> GetOrdersWithMissingYearAsync(CancellationToken ct = default);
    /// <summary>Empty input returns an empty list without a round trip.</summary>
    Task<IReadOnlyList<Order>> GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
        IReadOnlyCollection<Guid> campSeasonIds,
        CancellationToken ct = default);

    /// <summary>
    /// Null when none; the first match when the service-enforced one-per-team-per-year rule was broken.
    /// </summary>
    Task<Order?> GetOrderForTeamAsync(Guid teamId, int year, CancellationToken ct = default);

    /// <summary>Empty input returns an empty list without a round trip.</summary>
    Task<IReadOnlyList<Order>> GetOrdersForTeamsWithLinesAndPaymentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        int year,
        CancellationToken ct = default);

    /// <summary>
    /// By persisted <see cref="Order.Year"/>, plus legacy <c>Year = 0</c> rows in <paramref name="campSeasonIds"/>. Not
    /// selected through the counterparty, so the accounting export never loses a row that carries money.
    /// </summary>
    Task<IReadOnlyList<Order>> GetOrdersForYearWithLinesAndPaymentsAsync(
        int year,
        IReadOnlyCollection<Guid> campSeasonIds,
        CancellationToken ct = default);

    Task AddOrderAsync(Order order, CancellationToken ct = default);
    Task UpdateOrderAsync(Order order, CancellationToken ct = default);
    /// <summary>Cascades to lines and payments; the service enforces the preconditions.</summary>
    Task DeleteOrderAsync(Guid orderId, CancellationToken ct = default);

    Task AddLineAsync(OrderLine line, CancellationToken ct = default);
    Task RemoveLineAsync(Guid lineId, CancellationToken ct = default);
    Task<LineContext?> GetLineWithOrderAndProductAsync(Guid lineId, CancellationToken ct = default);

    Task AddPaymentAsync(Payment payment, CancellationToken ct = default);
    Task<bool> StripePaymentIntentExistsAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>Null when none. The Stripe PI index is unique, so at most one row matches.</summary>
    Task<Payment?> GetPaymentByStripePaymentIntentIdAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>The bare write: the service owns the transition rules.</summary>
    Task UpdatePaymentStatusAsync(Guid paymentId, PaymentStatus status, CancellationToken ct = default);

    Task<IReadOnlyList<Payment>> GetPaymentsMissingMethodNameAsync(CancellationToken ct = default);

    Task SetPaymentMethodNameAsync(Guid paymentId, PaymentMethod methodName, CancellationToken ct = default);

    /// <summary>The service enforces any status precondition.</summary>
    Task DeletePaymentAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>Rows with a Stripe PaymentIntent id, for missing-payment and orphan detection.</summary>
    Task<IReadOnlyList<RecordedStripePayment>> GetRecordedStripePaymentsAsync(CancellationToken ct = default);

    /// <summary>Empty input returns an empty list without a round trip.</summary>
    Task<IReadOnlyList<Invoice>> GetInvoicesForOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct = default);

    /// <summary>
    /// One <c>SaveChanges</c>: an order must never be <c>InvoiceIssued</c> without its invoice row, or carry one while
    /// still <c>Open</c>.
    /// </summary>
    Task SaveIssuedInvoiceAsync(Invoice invoice, Order order, CancellationToken ct = default);
}
