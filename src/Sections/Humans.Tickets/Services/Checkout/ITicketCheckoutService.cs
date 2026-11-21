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

    /// <summary>The buy page for an invited buyer's link; null when the link names no offer.</summary>
    /// <summary>Events on sale with today's public price, for a signed-out visitor; soonest first.</summary>
    Task<IReadOnlyList<PublicSale>> GetPublicSalesAsync(CancellationToken ct = default);

    Task<BuyPage?> GetDirectedBuyPageAsync(string token, Guid userId, CancellationToken ct = default);

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

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForOrderAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>Every ticket the person holds or held, any event and state, newest first.</summary>
    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>The resale queue and its history, oldest offer first.</summary>
    Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>The person's valid or offered tickets with their resale state, for "my tickets".</summary>
    Task<IReadOnlyList<HeldTicket>> GetHeldTicketsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The holder returns an adult ticket to the resale queue; its child tickets go with it.</summary>
    Task<CheckoutMutationResult> OfferForResaleAsync(Guid ticketId, Guid userId, CancellationToken ct = default);

    /// <summary>The holder takes an offered ticket back, unless a checkout is paying for it right now.</summary>
    Task<CheckoutMutationResult> WithdrawOfferAsync(Guid ticketId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Directed resale: emails one person a single-use link to buy this offered ticket ahead of the queue. The buyer
    /// still pays the association and gets a new ticket in their own name.
    /// </summary>
    Task<CheckoutMutationResult> InviteBuyerAsync(
        Guid ticketId, Guid userId, string inviteeEmail, Uri claimUrlBase, string culture, CancellationToken ct = default);

    Task<CheckoutMutationResult> CancelInviteAsync(Guid ticketId, Guid userId, CancellationToken ct = default);

    /// <summary>Refreshes every open order and sweeps the resale queue; returns how many changed.</summary>
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
    IReadOnlyList<TicketPriceRule> PriceRules)
{
    public long? ChildPriceMinor { get; init; }
    public int? ChildMaxAge { get; init; }
    public int? ResaleOpensAtSold { get; init; }
    public int DirectedResaleDays { get; init; } = 3;
    public Guid? TermsTeamId { get; init; }
}

/// <summary><paramref name="ResaleOnly"/>: only returned tickets are left.</summary>
internal sealed record PublicSale(string EventName, long PriceMinor, bool ResaleOnly);

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
    IReadOnlyList<TicketCheckoutOrder> Orders)
{
    /// <summary>The ticket rules the buyer has not yet accepted are the unsigned rows.</summary>
    public IReadOnlyList<Humans.Consent.Contracts.RequiredConsentRow> TermsToAccept { get; init; } = [];

    /// <summary>True when the person has never said yes or no to the mailing list.</summary>
    public bool AskMarketing { get; init; }
}

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
    string Culture)
{
    /// <summary>Legal names of children on the buyer's own ticket.</summary>
    public IReadOnlyList<string> ChildNames { get; init; } = [];

    public bool AcceptTerms { get; init; }

    public bool MarketingOptIn { get; init; }

    /// <summary>The token from a directed-resale link.</summary>
    public string? DirectedToken { get; init; }

    /// <summary>Recorded with the consent to the ticket rules.</summary>
    public string IpAddress { get; init; } = "unknown";

    public string UserAgent { get; init; } = string.Empty;
}

/// <summary><paramref name="Refusal"/> is a resource key; <paramref name="PaymentUrl"/> is null for a free order.</summary>
internal sealed record StartCheckoutResult(Guid? OrderId, Uri? PaymentUrl, string? Refusal)
{
    public static StartCheckoutResult Refused(string key) => new(null, null, key);
}

/// <summary><paramref name="Timeline"/> is oldest first.</summary>
internal sealed record CheckoutOrderDetail(
    TicketCheckoutOrder Order,
    IReadOnlyList<TicketCheckoutRefund> Refunds,
    IReadOnlyList<TicketCheckoutOrderEvent> Timeline,
    IReadOnlyList<TicketCheckoutTicket> Tickets)
{
    /// <summary>What may still be refunded: ticket lines only, net of every refund that did not fail.</summary>
    public long RefundableMinor =>
        Math.Max(0, Order.TicketTotalMinor - Math.Max(
            Order.RefundedMinor,
            Refunds.Where(r => r.State != RefundState.Failed).Sum(r => r.AmountMinor)));
}

/// <summary>
/// One of the person's tickets. <paramref name="QueuePosition"/> is 1-based among offers waiting to sell, null when not
/// offered. <paramref name="HasDependents"/>: valid child tickets that are returned with it.
/// </summary>
internal sealed record HeldTicket(
    TicketCheckoutTicket Ticket,
    string EventName,
    TicketResaleOffer? Offer,
    int? QueuePosition,
    bool CanOffer,
    bool HasDependents,
    bool DirectedActive);
