using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models;

public sealed class StoreIndexViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public IReadOnlyList<StoreCampSeasonOrders> CampSeasons { get; init; } = [];
}

public sealed class StoreOrderViewModel
{
    public OrderDto Order { get; init; } = null!;
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public string CampName { get; init; } = string.Empty;
    public bool CanEdit { get; init; }
    /// <summary>True when the camp lead may initiate Stripe Checkout against this order (auth + balance > 0 + Stripe configured).</summary>
    public bool CanPay { get; init; }
    /// <summary>False when STRIPE_STORE_KEY is unset; suppresses the Pay button + shows a disabled tooltip.</summary>
    public bool IsStripeConfigured { get; init; }

    public static StoreOrderViewModel FromPageData(StoreOrderPageData pageData) => new()
    {
        Order = pageData.Order,
        Catalog = pageData.Catalog,
        CampName = pageData.CampName,
        CanEdit = pageData.CanEdit,
        CanPay = pageData.CanPay,
        IsStripeConfigured = pageData.IsStripeConfigured
    };
}
