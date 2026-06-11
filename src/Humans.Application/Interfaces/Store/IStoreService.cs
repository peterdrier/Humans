using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Store;

public interface IStoreService : IApplicationService
{
    // Catalog (read)
    Task<StoreIndexData> GetIndexDataAsync(Guid userId, bool isPrivilegedReader, CancellationToken ct = default);
    Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default);
    Task<IReadOnlyList<ProductDto>> GetAllProductsForYearAsync(int year, CancellationToken ct = default);
    Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default);

    // Catalog (write — StoreAdmin)
    Task<Guid> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default);
    Task UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default);
    Task<StoreCatalogSaveResult> SaveProductWithResultAsync(StoreProductSaveRequest request, Guid actorUserId, CancellationToken ct = default);
    Task DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default);

    // Orders (camp lead)
    Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default);
    Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default);
    Task<StoreOrderPageData> GetOrderPageDataAsync(
        OrderDto order,
        bool canEdit,
        bool canPayAuthorized,
        CancellationToken ct = default);
    Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes the order. Permitted only when the order's balance is zero
    /// (no outstanding charges, no payments needing reversal). Authorization is
    /// enforced at the controller via <c>StoreOrderOperationRequirement.Delete</c>;
    /// the service rejects non-zero balances as a defense-in-depth invariant.
    /// </summary>
    Task DeleteOrderAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default);

    // Orders (team coordinator — non-billable)
    /// <summary>
    /// Creates a non-billable team order for <paramref name="teamId"/> in the
    /// active event year. The team must be a department (no <c>ParentTeamId</c>);
    /// only one open team order per team per year is permitted.
    /// </summary>
    Task<Guid> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Returns the team's order for the active event year, or null if none exists.
    /// </summary>
    Task<OrderDto?> GetOrderForTeamAsync(Guid teamId, CancellationToken ct = default);
    /// <summary>
    /// The product's <c>OrderableUntil</c> deadline is an authorization concern
    /// (<c>StoreOrderAuthorizationHandler</c> denies non-admin line edits past it); the service
    /// only annotates the audit entry when a line is edited past the deadline. The Open-state
    /// requirement is a service invariant: an issued invoice is frozen for everyone.
    /// </summary>
    Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default);
    Task<StoreMutationResult> AddLineWithResultAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default);
    /// <inheritdoc cref="AddLineAsync"/>
    Task RemoveLineAsync(Guid orderId, Guid lineId, Guid actorUserId, CancellationToken ct = default);
    Task<StoreMutationResult> RemoveLineWithResultAsync(Guid orderId, Guid lineId, Guid actorUserId, CancellationToken ct = default);

    // Counterparty (camp lead pre-issuance, FinanceAdmin always)
    Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default);
    Task<StoreMutationResult> UpdateCounterpartyWithResultAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default);

    // Payments (FinanceAdmin)
    Task RecordManualPaymentAsync(Guid orderId, decimal amountEur, StorePaymentMethod method, string? externalRef, string? notes, Guid actorUserId, CancellationToken ct = default);

    Task<string> CreateStripeCheckoutSessionAsync(
        OrderDto order,
        decimal amountEur,
        string returnUrl,
        CancellationToken ct = default);

    /// <summary>
    /// Insert a Stripe-method payment from a verified <c>checkout.session.completed</c> webhook.
    /// Idempotent on <paramref name="paymentIntentId"/> — duplicate webhook deliveries are no-ops.
    /// Audit-logged with job actor "StripeWebhook" (no human actor). <paramref name="status"/> is
    /// <see cref="StorePaymentStatus.Paid"/> for a settled sync payment (and for reconciliation,
    /// which only records confirmed-paid sessions) and <see cref="StorePaymentStatus.Pending"/> for
    /// an async mandate that has not yet cleared — a Pending row does not count toward the balance.
    /// </summary>
    Task RecordStripePaymentAsync(
        Guid orderId,
        string paymentIntentId,
        decimal amountEur,
        StorePaymentStatus status = StorePaymentStatus.Paid,
        CancellationToken ct = default);

    /// <summary>
    /// Handle a verified Store Stripe Checkout webhook event. The Stripe connector owns
    /// signature verification and parsing; Store owns payment-event interpretation.
    /// </summary>
    Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Builds the Stripe payment reconciliation report for the Store admin screen: webhook/checkout
    /// health, every Store Checkout Session matched to its order with a reconciliation status, and
    /// recorded Stripe payments orphaned from Stripe. Read-only — records nothing.
    /// </summary>
    Task<StripeReconciliationReport> GetStripeReconciliationAsync(CancellationToken ct = default);

    /// <summary>
    /// Records every paid, order-matched, not-yet-recorded Stripe Checkout Session via the idempotent
    /// <see cref="RecordStripePaymentAsync"/> path, pulling amounts from Stripe (never the caller).
    /// Skips unmatched and Team-owned orders. Writes one <c>StorePaymentsReconciled</c> audit entry.
    /// Safe to re-run. Returns how many were recorded and their total.
    /// </summary>
    Task<StripeReconciliationResult> RecordMissingStripePaymentsAsync(Guid actorUserId, CancellationToken ct = default);

    // Invoice issuance (FinanceAdmin) — implemented in Phase 5
    Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default);

    // Summary
    /// <summary>
    /// Builds the admin aggregate report (by-camp, by-item, camps x products cross-tab)
    /// for the given event year. Used by <c>/Store/Summary</c>.
    /// </summary>
    Task<StoreSummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default);
}

public sealed record StoreMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static StoreMutationResult Success { get; } = new(true, null);

    public static StoreMutationResult Failure(string message) => new(false, message);
}

public sealed record StoreProductSaveRequest(
    Guid? Id,
    int Year,
    string? Name,
    string? Description,
    decimal UnitPriceEur,
    decimal VatRatePercent,
    decimal? DepositAmountEur,
    string? OrderableUntil,
    bool IsActive);

public sealed record StoreCatalogSaveResult(
    bool Succeeded,
    bool Created,
    string? ErrorField,
    string? ErrorMessage)
{
    public static StoreCatalogSaveResult Success(bool created) => new(true, created, null, null);

    public static StoreCatalogSaveResult Failure(string? field, string message) => new(false, false, field, message);
}
