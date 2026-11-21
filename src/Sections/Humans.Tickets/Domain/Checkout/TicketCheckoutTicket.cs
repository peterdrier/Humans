using NodaTime;

namespace Humans.Tickets.Domain.Checkout;

/// <summary>
/// A named ticket issued from a paid checkout order: what the gate scans. Never renamed by a holder; resale voids it
/// and issues a new one in the buyer's name.
/// </summary>
internal sealed class TicketCheckoutTicket
{
    public Guid Id { get; init; }

    public Guid SalesEventId { get; init; }

    public Guid OrderId { get; init; }

    /// <summary>The order line this ticket was issued for; unique per order, so an order is never fulfilled twice.</summary>
    public int LineIndex { get; init; }

    public TicketHolderKind Kind { get; init; }

    /// <summary>The adult a child or companion ticket belongs to; it goes wherever the adult's ticket goes.</summary>
    public Guid? ParentTicketId { get; init; }

    public Guid? HolderUserId { get; set; }

    /// <summary>The legal name that must match ID at the gate.</summary>
    public string HolderName { get; set; } = string.Empty;

    public string? HolderEmail { get; set; }

    /// <summary>What the QR encodes; unique.</summary>
    public string Barcode { get; init; } = string.Empty;

    public long PricePaidMinor { get; init; }

    public string WaveName { get; init; } = string.Empty;

    /// <summary>The class price rule's program, null when wave priced.</summary>
    public Guid? ClassProgramId { get; init; }

    public string? ClassLabel { get; init; }

    public long CreditsMinor { get; init; }

    public TicketCheckoutTicketState State { get; set; }

    public Instant IssuedAt { get; init; }

    public Instant? VoidedAt { get; set; }

    public string? VoidReason { get; set; }
}

internal enum TicketHolderKind
{
    Adult = 0,
    Child = 1,
    Companion = 2,
}

internal enum TicketCheckoutTicketState
{
    Valid = 0,
    /// <summary>Returned to the resale queue; still valid at the gate until it sells.</summary>
    Offered = 1,
    /// <summary>Sold on to someone else, who holds a new ticket.</summary>
    Resold = 2,
    Void = 3,
}

/// <summary>
/// A holder's ticket waiting in the resale queue, oldest first. When it sells, the buyer gets a new ticket, the old one
/// is marked resold and the holder is refunded <see cref="RefundOwedMinor"/>. A directed offer (an invited buyer)
/// sells only to that email until <see cref="DirectedExpiresAt"/>, then rejoins the queue at its original place.
/// </summary>
internal sealed class TicketResaleOffer
{
    public Guid Id { get; init; }

    public Guid SalesEventId { get; init; }

    public Guid TicketId { get; init; }

    public Guid HolderUserId { get; set; }

    public Instant OfferedAt { get; init; }

    public TicketResaleOfferState State { get; set; }

    /// <summary>What the holder paid for the ticket, refunded when it sells.</summary>
    public long RefundOwedMinor { get; init; }

    /// <summary>The checkout holding the offer while it is <see cref="TicketResaleOfferState.Reserved"/>.</summary>
    public Guid? ReservedByOrderId { get; set; }

    public Guid? SoldToOrderId { get; set; }

    public Instant? SoldAt { get; set; }

    public Instant? WithdrawnAt { get; set; }

    /// <summary>The holder's refund once requested.</summary>
    public Guid? RefundId { get; set; }

    public string? DirectedEmail { get; set; }

    /// <summary>SHA-256 of the emailed link's token; the token itself is never stored.</summary>
    public string? DirectedTokenHash { get; set; }

    public Instant? DirectedExpiresAt { get; set; }

    public Instant? DirectedUsedAt { get; set; }

    /// <summary>Invitations sent for this offer, capped so a holder cannot mail addresses without end.</summary>
    public int DirectedInviteCount { get; set; }

    public bool IsDirectedTo(Instant now) => DirectedTokenHash is not null && DirectedUsedAt is null && DirectedExpiresAt > now;
}

internal enum TicketResaleOfferState
{
    Open = 0,
    /// <summary>Held by an unpaid checkout; back to Open if that checkout lapses.</summary>
    Reserved = 1,
    Sold = 2,
    Withdrawn = 3,
}
