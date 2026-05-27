using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public sealed record StoreIndexData(
    int Year,
    IReadOnlyList<ProductDto> Catalog,
    IReadOnlyList<StoreCounterpartyOrders> Counterparties,
    bool ShowNoOrdersMessage);

public sealed record StoreCounterpartyOrders(
    StoreOrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string DisplayName,
    int Year,
    IReadOnlyList<OrderDto> Orders);

public sealed record StoreOrderPageData(
    OrderDto Order,
    IReadOnlyList<ProductDto> Catalog,
    string CounterpartyDisplayName,
    bool CanEdit,
    bool CanPay,
    bool IsStripeConfigured);
