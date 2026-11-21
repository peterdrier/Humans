using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Tickets.Domain.Checkout;

/// <summary>
/// One purchase in Humans, whichever backend takes the money. The lines are Humans' price; the backend charges them
/// as given. Paid ticket lines are the named tickets.
/// </summary>
internal sealed class TicketCheckoutOrder
{
    public Guid Id { get; init; }

    /// <summary>The short code a buyer quotes to the ticketing team and that receipts print.</summary>
    public string Reference => Id.ToString("N")[..8].ToUpperInvariant();

    public Guid SalesEventId { get; init; }

    /// <summary>The signed-in payer.</summary>
    public Guid BuyerUserId { get; init; }

    public string BuyerName { get; set; } = string.Empty;

    public string BuyerEmail { get; set; } = string.Empty;

    /// <summary>The backend the order was opened on; never changes, even when the event's setting does.</summary>
    public string BackendKey { get; init; } = string.Empty;

    /// <summary>Null until the backend accepted the checkout.</summary>
    public string? BackendOrderId { get; set; }

    public string? PaymentUrl { get; set; }

    /// <summary>The order in the backend's own admin, for the ticketing team.</summary>
    public string? AdminUrl { get; set; }

    public TicketCheckoutOrderState State { get; set; }

    public string Currency { get; init; } = "EUR";

    /// <summary>The UI culture the buyer checked out in, for their receipt and ticket mail.</summary>
    public string Culture { get; init; } = "en";

    public long TotalMinor { get; init; }

    public long? FeeMinor { get; set; }

    public string? PaymentMethod { get; set; }

    public Instant? PaidAt { get; set; }

    /// <summary>As the backend reports it, however the refund was issued.</summary>
    public long RefundedMinor { get; set; }

    /// <summary>As the backend reports it: taken back by the buyer's bank in a chargeback not (yet) won.</summary>
    public long DisputedMinor { get; set; }

    /// <summary>The legal repo folder of the ticket rules the buyer accepted; null when the sale had none.</summary>
    public string? TermsFolder { get; init; }

    /// <summary>The commit of <see cref="TermsFolder"/> whose text the buyer was shown and accepted.</summary>
    public string? TermsVersion { get; init; }

    /// <summary>When the buyer was emailed that the event is cancelled; set once, after the order's cancellation work.</summary>
    public Instant? CancellationEmailedAt { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant ExpiresAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public List<TicketCheckoutOrderLine> Lines { get; set; } = [];

    public long TicketTotalMinor => Lines.Where(l => l.Kind == CheckoutLineKind.Ticket).Sum(l => l.AmountMinor);

    /// <summary>Holds capacity: paid, a payment on its way, or still payable.</summary>
    public bool HoldsTickets(Instant now) =>
        State is TicketCheckoutOrderState.Paid or TicketCheckoutOrderState.Processing
        || (State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending && ExpiresAt > now);

    /// <summary>Not yet settled either way.</summary>
    public bool IsOpen => State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending
        or TicketCheckoutOrderState.Processing;
}

internal enum TicketCheckoutOrderState
{
    /// <summary>Saved, backend not yet called (or the call failed).</summary>
    Created = 0,
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Expired = 4,
    Cancelled = 5,

    /// <summary>Paid with a method that settles later; holds its tickets past <c>ExpiresAt</c> and cannot be cancelled.</summary>
    Processing = 6,
}

internal sealed class TicketCheckoutOrderLine
{
    public CheckoutLineKind Kind { get; set; }

    public string Description { get; set; } = string.Empty;

    public long AmountMinor { get; set; }

    public decimal VatRate { get; set; }

    /// <summary>The legal name on the ticket; null on donations.</summary>
    public string? AttendeeName { get; set; }

    public string? AttendeeEmail { get; set; }

    /// <summary>The ticket holder when known; null for a child or a companion issued without an account.</summary>
    public Guid? AttendeeUserId { get; set; }

    public TicketHolderKind? HolderKind { get; set; }

    /// <summary>For a child line, the index of the adult line it belongs to in the same order.</summary>
    public int? ParentLineIndex { get; set; }

    public string? WaveName { get; set; }

    public Guid? ClassProgramId { get; set; }

    public string? ClassLabel { get; set; }

    public long CreditsMinor { get; set; }

    /// <summary>Set when this line buys a returned ticket instead of fresh stock.</summary>
    public Guid? ResaleOfferId { get; set; }

    public TicketDonationPurpose? DonationPurpose { get; set; }
}

internal enum TicketDonationPurpose
{
    General = 0,
    Creativity = 1,
    LowIncomePool = 2,
}

internal sealed class TicketCheckoutRefund
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }

    public long AmountMinor { get; init; }

    public string Reason { get; init; } = string.Empty;

    public RefundState State { get; set; }

    public string? BackendRefundId { get; set; }

    public string? FailureReason { get; set; }

    public string? ManualRefundUrl { get; set; }

    /// <summary>Null for automation (the refund owed when a returned ticket resells).</summary>
    public Guid? RequestedByUserId { get; init; }

    public Instant CreatedAt { get; init; }
}

/// <summary>
/// One step in an order's life, append-only: never updated or deleted, so customer service can see what happened and
/// who did it. <see cref="ActorUserId"/> is null for automation (a webhook, the reconcile poll); <see cref="Detail"/>
/// carries amounts and states, never names or emails.
/// </summary>
internal sealed class TicketCheckoutOrderEvent
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }

    public Instant At { get; init; }

    public TicketCheckoutOrderEventKind Kind { get; init; }

    public string Detail { get; init; } = string.Empty;

    public Guid? ActorUserId { get; set; }
}

internal enum TicketCheckoutOrderEventKind
{
    Created = 0,
    CheckoutOpened = 1,
    BackendRefused = 2,
    StateChanged = 3,
    RefundedTotalChanged = 4,
    RefundRequested = 5,
    RefundSettled = 6,
    Cancelled = 7,
    TicketsIssued = 8,
    ResaleOffered = 9,
    ResaleWithdrawn = 10,
    ResaleSold = 11,
    ResaleInvited = 12,
    ResaleInviteUsed = 13,
    ResaleInviteLapsed = 14,
    ResaleInviteCancelled = 15,
    Renamed = 16,
    Voided = 17,
    CompanionIssued = 18,
    TermsAccepted = 19,
    TicketsEmailed = 20,
    TicketsResent = 21,
    TicketMailSkipped = 22,
    CompanionRequested = 23,
    CompanionRequestDecided = 24,
    EventCancelled = 25,
    Restored = 26,
}
