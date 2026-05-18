namespace Humans.Application.Services.Store.Dtos;

public sealed record StoreSummaryDto(
    int Year,
    IReadOnlyList<OrderSummaryDto> ByCamp,
    IReadOnlyList<ProductAggregateDto> ByItem,
    StoreCrossTabDto CrossTab);

public sealed record ProductAggregateDto(
    Guid ProductId,
    string ProductName,
    int TotalQty,
    decimal TotalRevenueEur);

public sealed record StoreCrossTabDto(
    IReadOnlyList<StoreCrossTabColumn> Products,
    IReadOnlyList<StoreCrossTabRow> Camps);

public sealed record StoreCrossTabColumn(
    Guid ProductId,
    string ProductName,
    int TotalQty);

public sealed record StoreCrossTabRow(
    Guid CampSeasonId,
    string CampName,
    int TotalQty,
    IReadOnlyDictionary<Guid, int> QtyByProduct);
