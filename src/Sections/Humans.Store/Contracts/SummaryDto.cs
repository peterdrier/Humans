namespace Humans.Store.Contracts;

internal sealed record SummaryDto(
    int Year,
    IReadOnlyList<OrderSummaryDto> ByCounterparty,
    IReadOnlyList<ProductAggregateDto> ByItem,
    CrossTabDto CrossTab);

internal sealed record ProductAggregateDto(
    Guid ProductId,
    string ProductName,
    int TotalQty,
    decimal TotalRevenueEur);

internal sealed record CrossTabDto(
    IReadOnlyList<CrossTabColumn> Products,
    IReadOnlyList<CrossTabRow> Counterparties);

internal sealed record CrossTabColumn(
    Guid ProductId,
    string ProductName,
    int TotalQty);

internal sealed record CrossTabRow(
    OrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string CounterpartyName,
    int TotalQty,
    IReadOnlyDictionary<Guid, int> QtyByProduct);
