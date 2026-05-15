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
    Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default);
    Task<StoreMutationResult> AddLineWithResultAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default);
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
    /// Audit-logged with job actor "StripeWebhook" (no human actor).
    /// </summary>
    Task RecordStripePaymentAsync(Guid orderId, string paymentIntentId, decimal amountEur, CancellationToken ct = default);

    /// <summary>
    /// Handle a verified Store Stripe Checkout webhook event. The Stripe connector owns
    /// signature verification and parsing; Store owns payment-event interpretation.
    /// </summary>
    Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default);

    // Invoice issuance (FinanceAdmin) — implemented in Phase 5
    Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default);

    // Summary
    Task<IReadOnlyList<OrderSummaryDto>> GetAllOrderSummariesAsync(int year, CancellationToken ct = default);
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
