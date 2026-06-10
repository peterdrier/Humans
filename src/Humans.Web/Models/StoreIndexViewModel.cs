using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models;

public sealed class StoreIndexViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public IReadOnlyList<StoreCounterpartyOrders> Counterparties { get; init; } = [];
    /// <summary>
    /// Keyed by <see cref="StoreCounterpartyOrders.CounterpartyId"/>: whether the viewer may act on that
    /// counterparty's order (Create when it has none, Delete when it does). Privileged readers see
    /// counterparties they cannot manage (e.g. a TeamsAdmin sees camp orders read-only), so per-row gating
    /// is resolved against the order authorization handler in the controller rather than a blanket admin flag.
    /// </summary>
    public IReadOnlyDictionary<Guid, bool> CanManageByCounterparty { get; init; } =
        new Dictionary<Guid, bool>();
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

    /// <summary>
    /// Line ids whose Remove button renders, resolved per line against the order authorization
    /// handler in the controller: past the product's order deadline only Store admins qualify.
    /// </summary>
    public IReadOnlyCollection<Guid> RemovableLineIds { get; init; } = [];

    /// <summary>Price-change audit events for this order's items since it was created (#816).</summary>
    public IReadOnlyList<AuditLogEntrySnapshot> PriceChanges { get; init; } = [];

    public static StoreOrderViewModel FromPageData(
        StoreOrderPageData pageData,
        bool canDelete,
        IReadOnlyList<ProductDto> catalog,
        IReadOnlyCollection<Guid> removableLineIds) => new()
        {
            Order = pageData.Order,
            Catalog = catalog,
            CounterpartyDisplayName = pageData.CounterpartyDisplayName,
            CanEdit = pageData.CanEdit,
            RemovableLineIds = removableLineIds,
            CanPay = pageData.CanPay,
            IsStripeConfigured = pageData.IsStripeConfigured,
            CanDelete = canDelete,
            PriceChanges = pageData.PriceChanges
        };
}
