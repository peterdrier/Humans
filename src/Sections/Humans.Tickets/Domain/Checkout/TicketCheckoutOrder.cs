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

    public long TotalMinor { get; init; }

    public long? FeeMinor { get; set; }

    public string? PaymentMethod { get; set; }

    public Instant? PaidAt { get; set; }

    /// <summary>As the backend reports it, however the refund was issued.</summary>
    public long RefundedMinor { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant ExpiresAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public List<TicketCheckoutOrderLine> Lines { get; set; } = [];

    public long TicketTotalMinor => Lines.Where(l => l.Kind == CheckoutLineKind.Ticket).Sum(l => l.AmountMinor);

    /// <summary>Holds capacity: paid, or still payable.</summary>
    public bool HoldsTickets(Instant now) =>
        State == TicketCheckoutOrderState.Paid
        || (State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending && ExpiresAt > now);
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

    /// <summary>The ticket holder when known; null for a partner who has no account yet.</summary>
    public Guid? AttendeeUserId { get; set; }

    /// <summary>The scannable code, issued when the order is paid.</summary>
    public string? Barcode { get; set; }

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

    public Guid RequestedByUserId { get; init; }

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
}
