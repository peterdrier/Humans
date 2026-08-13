namespace Humans.Store.Contracts;

public sealed record SummaryDto(
    int Year,
    IReadOnlyList<OrderSummaryDto> ByCounterparty,
    IReadOnlyList<ProductAggregateDto> ByItem,
    CrossTabDto CrossTab);

public sealed record ProductAggregateDto(
    Guid ProductId,
    string ProductName,
    int TotalQty,
    decimal TotalRevenueEur);

public sealed record CrossTabDto(
    IReadOnlyList<CrossTabColumn> Products,
    IReadOnlyList<CrossTabRow> Counterparties);

public sealed record CrossTabColumn(
    Guid ProductId,
    string ProductName,
    int TotalQty);

public sealed record CrossTabRow(
    OrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string CounterpartyName,
    int TotalQty,
    IReadOnlyDictionary<Guid, int> QtyByProduct);
