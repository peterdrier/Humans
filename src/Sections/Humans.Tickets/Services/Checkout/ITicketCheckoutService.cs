using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// The checkout lane: Humans prices the order, the event's backend takes the money. Authorization is the controllers'
/// job; every method trusts its actor.
/// </summary>
internal interface ITicketCheckoutService : ITicketCheckoutEvents, IApplicationService
{
    IReadOnlyList<CheckoutBackendInfo> Backends { get; }

    Task<IReadOnlyList<TicketSalesEvent>> GetSalesEventsAsync(CancellationToken ct = default);

    Task<TicketSalesEvent?> GetSalesEventAsync(Guid id, CancellationToken ct = default);

    Task<CheckoutMutationResult> SaveSalesEventAsync(SalesEventSave save, Guid actorUserId, CancellationToken ct = default);

    /// <summary>One page per event selling through Humans (not Closed); several events may sell at once.</summary>
    Task<IReadOnlyList<BuyPage>> GetBuyPagesAsync(Guid userId, CancellationToken ct = default);

    Task<StartCheckoutResult> StartCheckoutAsync(StartCheckout command, Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    Task<CheckoutOrderDetail?> GetOrderDetailAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>Asks the order's backend for its state and applies it. Null when the order does not exist.</summary>
    Task<TicketCheckoutOrder?> RefreshOrderAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Refunds an exact amount of a paid order's ticket lines through its backend. Never more than the ticket lines
    /// cost, counting every earlier refund that did not fail: donations are not refunded.
    /// </summary>
    Task<CheckoutMutationResult> RefundAsync(
        Guid orderId, long amountMinor, string reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The buyer abandons an unpaid order.</summary>
    Task<CheckoutMutationResult> CancelAsync(Guid orderId, Guid userId, CancellationToken ct = default);

    /// <summary>Refreshes every open order; returns how many changed state.</summary>
    Task<int> ReconcileOpenOrdersAsync(CancellationToken ct = default);
}

internal sealed record CheckoutBackendInfo(string Key, bool IsConfigured);

/// <summary>Admin pages are English-only, so the refusal is shown as is.</summary>
internal sealed record CheckoutMutationResult(string? Refusal = null)
{
    public static readonly CheckoutMutationResult Ok = new();

    public bool Succeeded => Refusal is null;
}

internal sealed record SalesEventSave(
    Guid? Id,
    Guid EventSettingsId,
    string BackendKey,
    string? BackendEventRef,
    TicketSalesMode Mode,
    NodaTime.Instant? SalesCloseAt,
    int Capacity,
    decimal TicketVatRate,
    IReadOnlyList<TicketSalesWave> Waves,
    IReadOnlyList<long> SupporterTotalsMinor,
    IReadOnlyList<TicketPriceRule> PriceRules);

/// <summary>
/// What the buyer sees. <paramref name="Unavailable"/> is a resource key naming why buying is closed to them, null
/// when they can buy. <paramref name="PublicPrice"/> is what a partner pays.
/// </summary>
internal sealed record BuyPage(
    TicketSalesEvent SalesEvent,
    string EventName,
    TicketPrice? OwnPrice,
    TicketPrice? PublicPrice,
    string? Unavailable,
    bool AlreadyHoldsTicket,
    string BuyerName,
    IReadOnlyList<TicketCheckoutOrder> Orders);

/// <summary>
/// <paramref name="SupporterTotalMinor"/> is one of the event's VIP totals: the part above the buyer's own ticket is a
/// donation. <paramref name="DonationMinor"/> is a free donation on top, for <paramref name="DonationPurpose"/>. A
/// partner always pays the public price.
/// </summary>
internal sealed record StartCheckout(
    Guid SalesEventId,
    bool ForSelf,
    string? PartnerName,
    string? PartnerEmail,
    long? SupporterTotalMinor,
    long DonationMinor,
    TicketDonationPurpose DonationPurpose,
    Uri ReturnUrlBase,
    string Culture);

/// <summary><paramref name="Refusal"/> is a resource key; <paramref name="PaymentUrl"/> is null for a free order.</summary>
internal sealed record StartCheckoutResult(Guid? OrderId, Uri? PaymentUrl, string? Refusal)
{
    public static StartCheckoutResult Refused(string key) => new(null, null, key);
}

/// <summary><paramref name="Timeline"/> is oldest first.</summary>
internal sealed record CheckoutOrderDetail(
    TicketCheckoutOrder Order,
    IReadOnlyList<TicketCheckoutRefund> Refunds,
    IReadOnlyList<TicketCheckoutOrderEvent> Timeline)
{
    /// <summary>What may still be refunded: ticket lines only, net of every refund that did not fail.</summary>
    public long RefundableMinor =>
        Math.Max(0, Order.TicketTotalMinor - Math.Max(
            Order.RefundedMinor,
            Refunds.Where(r => r.State != RefundState.Failed).Sum(r => r.AmountMinor)));
}
