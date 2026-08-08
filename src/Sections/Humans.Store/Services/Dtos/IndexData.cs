using Humans.Application.Interfaces.AuditLog;
using Humans.Store.Domain;

namespace Humans.Store.Services.Dtos;

internal sealed record IndexData(
    int Year,
    IReadOnlyList<ProductDto> Catalog,
    IReadOnlyList<CounterpartyOrders> Counterparties,
    bool ShowNoOrdersMessage);

internal sealed record CounterpartyOrders(
    OrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string DisplayName,
    int Year,
    IReadOnlyList<OrderDto> Orders);

internal sealed record OrderPageData(
    OrderDto Order,
    IReadOnlyList<ProductDto> Catalog,
    string CounterpartyDisplayName,
    bool CanEdit,
    bool CanPay,
    bool IsStripeConfigured,
    IReadOnlyList<AuditLogEntrySnapshot> PriceChanges);
