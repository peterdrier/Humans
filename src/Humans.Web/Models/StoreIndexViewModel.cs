using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models;

public sealed class StoreIndexViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public IReadOnlyList<StoreCounterpartyOrders> Counterparties { get; init; } = [];
    /// <summary>True when the viewer can administer the Store (StoreAdmin / FinanceAdmin / Admin). Surfaces per-row admin affordances such as Delete.</summary>
    public bool IsAdmin { get; init; }
}

public sealed class StoreOrderViewModel
{
    public OrderDto Order { get; init; } = null!;
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public string CounterpartyDisplayName { get; init; } = string.Empty;
    public bool CanEdit { get; init; }
    /// <summary>True when the camp lead may initiate Stripe Checkout against this order (auth + balance > 0 + Stripe configured).</summary>
    public bool CanPay { get; init; }
    /// <summary>False when STRIPE_STORE_KEY is unset; suppresses the Pay button + shows a disabled tooltip.</summary>
    public bool IsStripeConfigured { get; init; }
    /// <summary>True when the current user is a Store admin AND the order's balance is zero. Surfaces the Delete button.</summary>
    public bool CanDelete { get; init; }

    public static StoreOrderViewModel FromPageData(StoreOrderPageData pageData, bool canDelete) => new()
    {
        Order = pageData.Order,
        Catalog = pageData.Catalog,
        CounterpartyDisplayName = pageData.CounterpartyDisplayName,
        CanEdit = pageData.CanEdit,
        CanPay = pageData.CanPay,
        IsStripeConfigured = pageData.IsStripeConfigured,
        CanDelete = canDelete
    };
}
