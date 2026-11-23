using NodaTime;

namespace Humans.Tickets.Domain.Checkout;

/// <summary>
/// A named ticket issued from a paid order: what the gate scans. Never renamed by a holder; resale voids it
/// and issues a new one in the buyer's name.
/// </summary>
internal sealed class TicketCheckoutTicket
{
    public Guid Id { get; init; }

    public Guid TicketingSettingsId { get; init; }

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

    public long DiscountsMinor { get; init; }

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
    /// <summary>Put up for resale; the gate refuses it while it can still sell (the holder withdraws it to enter).</summary>
    Offered = 1,
    /// <summary>Sold on to someone else, who holds a new ticket.</summary>
    Resold = 2,
    Void = 3,
}

/// <summary>
/// A holder's ticket waiting in the resale queue, oldest first. When it sells, the buyer gets a new ticket, the old one
/// is marked resold and the evening refund batch refunds the holder <see cref="RefundOwedMinor"/> once the buyer's
/// payment has held. A directed offer also sells through the link the holder emailed to one or more people, and stays
/// in the queue meanwhile: whoever buys first gets it, and anyone following a link later buys the next ticket in the
/// queue. The links have no expiry.
/// </summary>
internal sealed class TicketResaleOffer
{
    public Guid Id { get; init; }

    public Guid TicketingSettingsId { get; init; }

    public Guid TicketId { get; init; }

    public Guid HolderUserId { get; set; }

    public Instant OfferedAt { get; init; }

    public TicketResaleOfferState State { get; set; }

    /// <summary>What the holder paid for the ticket, refunded when it sells.</summary>
    public long RefundOwedMinor { get; init; }

    /// <summary>The unpaid order holding the offer while it is <see cref="TicketResaleOfferState.Reserved"/>.</summary>
    public Guid? ReservedByOrderId { get; set; }

    public Guid? SoldToOrderId { get; set; }

    public Instant? SoldAt { get; set; }

    public Instant? WithdrawnAt { get; set; }

    /// <summary>The holder's refund once the evening batch requested it.</summary>
    public Guid? RefundId { get; set; }

    /// <summary>
    /// The people the holder emailed a link to. Kept after the offer sells, so a later link still finds its way to the
    /// next ticket in the queue.
    /// </summary>
    public List<TicketResaleInvite> Invites { get; set; } = [];

    public Instant? DirectedUsedAt { get; set; }

    /// <summary>Invitations sent for this offer, capped so a holder cannot mail addresses without end.</summary>
    public int DirectedInviteCount { get; set; }

    /// <summary>When the holder was told at sales close that it did not sell; once only.</summary>
    public Instant? UnsoldNotifiedAt { get; set; }

    public bool HasLiveInvite => Invites.Count > 0 && State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved;

    public TicketResaleInvite? InviteFor(string tokenHash) =>
        Invites.FirstOrDefault(i => string.Equals(i.TokenHash, tokenHash, StringComparison.Ordinal));
}

/// <summary>One emailed link: bound to the address it went to. The token itself is never stored.</summary>
internal sealed class TicketResaleInvite
{
    public string Email { get; set; } = string.Empty;

    /// <summary>SHA-256 of the link's token, hex.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Instant SentAt { get; set; }
}

internal enum TicketResaleOfferState
{
    Open = 0,
    /// <summary>Held by an unpaid order; back to Open if that order lapses.</summary>
    Reserved = 1,
    Sold = 2,
    Withdrawn = 3,
}
