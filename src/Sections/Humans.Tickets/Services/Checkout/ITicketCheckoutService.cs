using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// The checkout lane: Humans prices the order, the event's backend takes the money. Authorization is the controllers'
/// job; every method trusts its actor.
/// </summary>
internal interface ITicketCheckoutService : ITicketCheckoutEvents, IApplicationService
{
    IReadOnlyList<CheckoutBackendInfo> Backends { get; }

    /// <summary>Runs the backend's read-only check, for an event when its id in the backend is given; null for an unknown key.</summary>
    Task<IReadOnlyList<CheckoutBackendFinding>?> CheckBackendAsync(string key, string? backendEventRef, CancellationToken ct = default);

    Task<IReadOnlyList<TicketSalesEvent>> GetSalesEventsAsync(CancellationToken ct = default);

    Task<TicketSalesEvent?> GetSalesEventAsync(Guid id, CancellationToken ct = default);

    Task<CheckoutMutationResult> SaveSalesEventAsync(SalesEventSave save, Guid actorUserId, CancellationToken ct = default);

    /// <summary>One page per event selling through Humans (not Closed); several events may sell at once.</summary>
    Task<IReadOnlyList<BuyPage>> GetBuyPagesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Events on sale with today's public price, for a signed-out visitor; soonest first.</summary>
    Task<IReadOnlyList<PublicSale>> GetPublicSalesAsync(CancellationToken ct = default);

    /// <summary>The buy page for an invited buyer's link; null when the link names no offer.</summary>
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

    Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default);

    /// <summary>Appends a ticket delivery step (emailed, resent, skipped) to the order's timeline.</summary>
    Task RecordDeliveryAsync(Guid orderId, TicketCheckoutOrderEventKind kind, string detail, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>Every ticket the person holds or held, any event and state, newest first.</summary>
    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>The resale queue and its history, oldest offer first.</summary>
    Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>The person's valid or offered tickets with their resale state, and the valid child tickets that go with them, for "my tickets".</summary>
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

    /// <summary>
    /// The ticketing team's exception: a valid full-price adult ticket with no dependents moves to another named
    /// person, whose account is found or created.
    /// </summary>
    Task<CheckoutMutationResult> RenameTicketAsync(
        Guid ticketId, string holderName, string holderEmail, string reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Voids a valid or offered ticket and its dependents, withdrawing any open offer. Refunds stay separate.</summary>
    Task<CheckoutMutationResult> VoidTicketAsync(Guid ticketId, string reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Issues a free companion ticket attached to a valid adult ticket, on a zero-total order of its own, within
    /// capacity. The email is optional: a companion without one holds no account.
    /// </summary>
    Task<CheckoutMutationResult> IssueCompanionAsync(
        Guid parentTicketId, string holderName, string? holderEmail, string reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Refreshes every open order and sweeps the resale queue; returns how many changed.</summary>
    Task<int> ReconcileOpenOrdersAsync(CancellationToken ct = default);

    /// <summary>
    /// Financial reconciliation of one backend for [<paramref name="from"/>, <paramref name="to"/>): Humans' orders
    /// against the backend's own list. Reads only. Null for an unknown backend; a backend that cannot be read gives a
    /// result carrying the error, never a partial comparison.
    /// </summary>
    Task<TicketRecon?> ReconcileBackendAsync(string backendKey, Instant from, Instant to, CancellationToken ct = default);

    /// <summary>The backends some sales setup sells through, for the reconciliation page and its daily run.</summary>
    Task<IReadOnlyList<string>> GetBackendsInUseAsync(CancellationToken ct = default);
}

internal sealed record CheckoutBackendInfo(string Key, bool IsConfigured, CheckoutBackendSetup Setup);

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
    public string? TermsUrl { get; init; }
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
    /// <summary>The ticket rules the buyer accepts with each checkout; null for none.</summary>
    public string? TermsUrl { get; init; }

    /// <summary>True when the person has never said yes or no to the mailing list.</summary>
    public bool AskMarketing { get; init; }

    /// <summary>The buyer's own unpaid checkout still holding a ticket, which they can finish or cancel to start again.</summary>
    public TicketCheckoutOrder? OpenOrder { get; init; }
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
    /// <summary>
    /// What may still be refunded: ticket lines only, net of what has gone back (the backend's total, dashboard refunds
    /// included, or Humans' own settled refunds if the backend has not caught up), of refunds not settled yet, and of
    /// what a chargeback took.
    /// </summary>
    public long RefundableMinor =>
        Math.Max(0, Order.TicketTotalMinor - Order.DisputedMinor
            - Math.Max(Order.RefundedMinor, Refunds.Where(r => r.State == RefundState.Succeeded).Sum(r => r.AmountMinor))
            - Refunds.Where(r => r.State is RefundState.Pending or RefundState.ManualRequired).Sum(r => r.AmountMinor));
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
