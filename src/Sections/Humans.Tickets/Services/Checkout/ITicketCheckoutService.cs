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

    /// <summary>The ticketing team opens or pauses resale by hand; returned tickets wait in the queue while it is paused.</summary>
    Task<CheckoutMutationResult> SetResaleOpenAsync(Guid salesEventId, bool open, Guid actorUserId, CancellationToken ct = default);

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
    /// person, whose account is found or created. With <paramref name="overrideRules"/> (Admin only) a class
    /// price or credit ticket may move too.
    /// </summary>
    Task<CheckoutMutationResult> RenameTicketAsync(
        Guid ticketId, string holderName, string holderEmail, string reason, bool overrideRules, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Voids a valid or offered ticket and its dependents, withdrawing any open offer, and refunds
    /// <paramref name="refundMinor"/> of its order first; zero voids with no refund, said so on the timeline and audit.
    /// A refund the backend declines leaves the ticket valid.
    /// </summary>
    Task<CheckoutMutationResult> VoidTicketAsync(
        Guid ticketId, string reason, long refundMinor, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Admin override: a voided ticket works again, unless its holder holds another or the event is cancelled.</summary>
    Task<CheckoutMutationResult> RestoreTicketAsync(Guid ticketId, string reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The ticketing team takes a holder's ticket out of the resale queue; the holder keeps it.</summary>
    Task<CheckoutMutationResult> WithdrawOfferForHolderAsync(Guid offerId, string reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Super admin only, never undone: closes sales for good; the reconcile job then voids every ticket, refunds every
    /// paid order what its tickets cost (donations stay) and emails each buyer.
    /// </summary>
    Task<CheckoutMutationResult> CancelSalesEventAsync(Guid salesEventId, string reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>A cancellation's progress and money, or what cancelling now would do; null for an unknown sales event.</summary>
    Task<CancellationProgress?> GetCancellationProgressAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>
    /// Issues a free companion ticket attached to a valid adult ticket, on a zero-total order of its own, within
    /// capacity, or past it with <paramref name="overrideCapacity"/> (Admin only). The email is optional: a
    /// companion without one holds no account.
    /// </summary>
    Task<CheckoutMutationResult> IssueCompanionAsync(
        Guid parentTicketId, string holderName, string? holderEmail, string reason, bool overrideCapacity, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// The holder of a valid adult ticket asks for a free companion who is already in Humans, found by the address they
    /// verified there, with a short note for the ticketing team. Refusals are resource keys.
    /// </summary>
    Task<CheckoutMutationResult> RequestCompanionAsync(
        Guid ticketId, Guid holderUserId, string companionEmail, string note, CancellationToken ct = default);

    /// <summary>The holder takes back their request while it waits. Refusals are resource keys.</summary>
    Task<CheckoutMutationResult> WithdrawCompanionRequestAsync(Guid ticketId, Guid holderUserId, CancellationToken ct = default);

    /// <summary>Every companion request on the sales event, newest first, with names for the team's queue.</summary>
    Task<IReadOnlyList<CompanionRequestRow>> GetCompanionRequestsAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>
    /// The team's light check: approving issues the companion ticket (as <see cref="IssueCompanionAsync"/>, to the
    /// companion's own account), declining tells the holder why. Audited either way.
    /// </summary>
    Task<CheckoutMutationResult> DecideCompanionRequestAsync(
        Guid requestId, bool approve, string? note, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Refreshes every open order and sweeps the resale queue; returns how many changed.</summary>
    Task<int> ReconcileOpenOrdersAsync(CancellationToken ct = default);

    /// <summary>
    /// Financial reconciliation of one backend for [<paramref name="from"/>, <paramref name="to"/>): Humans' orders
    /// against the backend's own list. Reads only. Null for an unknown backend; a backend that cannot be read gives a
    /// result carrying the error, never a partial comparison.
    /// </summary>
    Task<TicketRecon?> ReconcileBackendAsync(string backendKey, Instant from, Instant to, CancellationToken ct = default);

    /// <summary>Every order opened on one backend, newest first, with its stuck orders and unsettled refunds; null for an unknown key.</summary>
    Task<BackendLedger?> GetBackendLedgerAsync(string backendKey, CancellationToken ct = default);

    /// <summary>The backends some sales setup sells through, for the reconciliation page and its daily run.</summary>
    Task<IReadOnlyList<string>> GetBackendsInUseAsync(CancellationToken ct = default);
}

internal sealed record CheckoutBackendInfo(string Key, bool IsConfigured, CheckoutBackendSetup Setup);

/// <summary>
/// <paramref name="Paid"/>: paid orders as reconciliation counts them, free orders left out. <paramref name="Stuck"/>:
/// orders still open well past their expiry, which neither a webhook nor the five-minute reconcile has settled.
/// <paramref name="UnsettledRefunds"/>: pending or waiting on a person. <paramref name="FailedRefunds"/>: failed, with
/// no later attempt on the order that went through.
/// </summary>
internal sealed record BackendLedger(
    CheckoutBackendInfo Backend,
    IReadOnlyList<TicketCheckoutOrder> Orders,
    IReadOnlyList<TicketCheckoutOrder> Paid,
    IReadOnlyList<TicketCheckoutOrder> Stuck,
    IReadOnlyList<TicketCheckoutRefund> UnsettledRefunds,
    IReadOnlyList<TicketCheckoutRefund> FailedRefunds);

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
    public TicketSalesSwitches Switches { get; init; } = new();

    /// <summary>The ticket rules folder in the legal repo, no leading or trailing slash.</summary>
    public string? TermsFolder { get; init; }
}

/// <summary><paramref name="ResaleOnly"/>: only returned tickets are left.</summary>
internal sealed record PublicSale(string EventName, long PriceMinor, bool ResaleOnly);

/// <summary>
/// What the buyer sees. <paramref name="Unavailable"/> is a resource key naming why buying is closed to them, null
/// when they can buy. <paramref name="PublicPrice"/> is what each other person on the order pays.
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
    /// <summary>
    /// The ticket rules the buyer accepts with each checkout; null for none, or while they cannot be read (then
    /// <c>Unavailable</c> says so).
    /// </summary>
    public TicketRules? Terms { get; init; }

    /// <summary>True when the person has never said yes or no to the mailing list.</summary>
    public bool AskMarketing { get; init; }

    /// <summary>The buyer may add other people to the order (<see cref="TicketSalesSwitches.PartnerTickets"/>).</summary>
    public bool OffersOthers { get; init; }

    /// <summary>The event takes donations at checkout (<see cref="TicketSalesSwitches.Donations"/>).</summary>
    public bool OffersDonations { get; init; }

    /// <summary>The buyer's own unpaid checkout still holding a ticket, which they can finish or cancel to start again.</summary>
    public TicketCheckoutOrder? OpenOrder { get; init; }
}

/// <summary>
/// <paramref name="SupporterTotalMinor"/> is one of the event's VIP totals: the part above the buyer's own ticket is a
/// donation. <paramref name="DonationMinor"/> is a free donation on top, for <paramref name="DonationPurpose"/>. Each
/// other person always pays the public price.
/// </summary>
internal sealed record StartCheckout(
    Guid SalesEventId,
    bool ForSelf,
    long? SupporterTotalMinor,
    long DonationMinor,
    TicketDonationPurpose DonationPurpose,
    Uri ReturnUrlBase,
    string Culture)
{
    /// <summary>Legal names of children on the buyer's own ticket; blank entries (unfilled form boxes) are ignored.</summary>
    public IReadOnlyList<string?> ChildNames { get; init; } = [];

    /// <summary>
    /// Addresses of other people already in Humans who each get an adult ticket in their own legal name; blank
    /// entries are ignored. Knowing the address someone verified in Humans is the light check that it is them.
    /// </summary>
    public IReadOnlyList<string?> OtherEmails { get; init; } = [];

    public bool AcceptTerms { get; init; }

    /// <summary>The ticket rules folder the buy page showed; with <see cref="TermsVersion"/> it names the text accepted.</summary>
    public string? TermsFolder { get; init; }

    /// <summary>The ticket rules version the buy page showed, which the order records.</summary>
    public string? TermsVersion { get; init; }

    public bool MarketingOptIn { get; init; }

    /// <summary>The token from a directed-resale link.</summary>
    public string? DirectedToken { get; init; }

}

/// <summary>
/// <paramref name="Refusal"/> is a resource key, formatted with <see cref="RefusalArgument"/> when it names one;
/// <paramref name="PaymentUrl"/> is null for a free order.
/// </summary>
internal sealed record StartCheckoutResult(Guid? OrderId, Uri? PaymentUrl, string? Refusal)
{
    /// <summary>What the refusal is about, such as the address of a person who already holds a ticket.</summary>
    public string? RefusalArgument { get; init; }

    public static StartCheckoutResult Refused(string key, string? argument = null) =>
        new(null, null, key) { RefusalArgument = argument };
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

    /// <summary>
    /// The refund a void suggests: what the ticket and the valid dependents voided with it cost on this order, within
    /// <see cref="RefundableMinor"/>. A dependent on another order (a companion) was paid there, so it adds nothing here.
    /// </summary>
    public long VoidRefundMinor(TicketCheckoutTicket ticket) =>
        Math.Min(RefundableMinor, ticket.PricePaidMinor + Tickets
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid).Sum(t => t.PricePaidMinor));
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
    bool DirectedActive)
{
    /// <summary>The event runs directed resale (<see cref="ResaleScheme.QueueAndDirected"/>).</summary>
    public bool CanInvite { get; init; }

    /// <summary>A valid adult ticket with no companion and no request waiting: the holder may ask for one.</summary>
    public bool CanAskCompanion { get; init; }

    /// <summary>The newest companion request on this ticket, if any.</summary>
    public TicketCompanionRequest? CompanionRequest { get; init; }
}

/// <summary>A companion request with the names the ticketing team needs to judge it.</summary>
internal sealed record CompanionRequestRow(
    TicketCompanionRequest Request,
    string HolderName,
    string CompanionName,
    string TicketBarcode,
    Guid TicketOrderId);
