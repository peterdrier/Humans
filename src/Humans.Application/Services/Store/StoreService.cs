using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Store;

public class StoreService : IStoreService
{
    private readonly IStoreRepository _repo;

    public StoreService(IStoreRepository repo) => _repo = repo;

    public Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2");

    public Task<Guid> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 3");

    public Task UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 3");

    public Task DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 3");

    public Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2");

    public Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2");

    public Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2");

    public Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2");

    public Task RemoveLineAsync(Guid lineId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2");

    public Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 2");

    public Task RecordManualPaymentAsync(Guid orderId, decimal amountEur, StorePaymentMethod method, string? externalRef, string? notes, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public Task<IReadOnlyList<OrderSummaryDto>> GetAllOrderSummariesAsync(int year, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");
}
