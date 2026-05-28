using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public sealed record StoreSummaryDto(
    int Year,
    IReadOnlyList<OrderSummaryDto> ByCounterparty,
    IReadOnlyList<ProductAggregateDto> ByItem,
    StoreCrossTabDto CrossTab);

public sealed record ProductAggregateDto(
    Guid ProductId,
    string ProductName,
    int TotalQty,
    decimal TotalRevenueEur);

public sealed record StoreCrossTabDto(
    IReadOnlyList<StoreCrossTabColumn> Products,
    IReadOnlyList<StoreCrossTabRow> Counterparties);

public sealed record StoreCrossTabColumn(
    Guid ProductId,
    string ProductName,
    int TotalQty);

public sealed record StoreCrossTabRow(
    StoreOrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string CounterpartyName,
    int TotalQty,
    IReadOnlyDictionary<Guid, int> QtyByProduct);
