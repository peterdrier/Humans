namespace Humans.Application.Services.Store.Dtos;

public sealed record StoreIndexData(
    int Year,
    IReadOnlyList<ProductDto> Catalog,
    IReadOnlyList<StoreCampSeasonOrders> CampSeasons,
    bool ShowNoCampOrdersMessage);

public sealed record StoreCampSeasonOrders(
    Guid CampSeasonId,
    string CampName,
    int Year,
    IReadOnlyList<OrderDto> Orders);

public sealed record StoreOrderPageData(
    OrderDto Order,
    IReadOnlyList<ProductDto> Catalog,
    string CampName,
    bool CanEdit,
    bool CanPay,
    bool IsStripeConfigured);
