using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models;

public sealed class StoreIndexViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public IReadOnlyList<StoreCampSeasonOrders> CampSeasons { get; init; } = [];
}

public sealed record StoreCampSeasonOrders(
    Guid CampSeasonId,
    string CampName,
    int Year,
    IReadOnlyList<OrderDto> Orders);

public sealed class StoreOrderViewModel
{
    public OrderDto Order { get; init; } = null!;
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public string CampName { get; init; } = string.Empty;
    public bool CanEdit { get; init; }
}
