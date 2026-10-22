using NodaTime;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Domain;

internal sealed class TicketOrder
{
    public Guid Id { get; init; }

    public string VendorOrderId { get; init; } = string.Empty;

    public string BuyerName { get; set; } = string.Empty;

    public string BuyerEmail { get; set; } = string.Empty;

    /// <summary>While set, sync must never write <see cref="BuyerName"/> or <see cref="BuyerEmail"/> again.</summary>
    public Instant? PiiErasedAt { get; set; }

    public Guid? MatchedUserId { get; set; }

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string? DiscountCode { get; set; }

    public TicketPaymentStatus PaymentStatus { get; set; }

    public string VendorEventId { get; set; } = string.Empty;

    public string? VendorDashboardUrl { get; set; }

    public Instant PurchasedAt { get; set; }

    public Instant SyncedAt { get; set; }

    /// <summary>From the vendor's <c>txn_id</c>; keys the Stripe fee lookup.</summary>
    public string? StripePaymentIntentId { get; set; }

    public string? PaymentMethod { get; set; }

    /// <summary>Card brand for cards, null for other methods.</summary>
    public string? PaymentMethodDetail { get; set; }

    public decimal? StripeFee { get; set; }

    /// <summary>Ticket Tailor's platform fee.</summary>
    public decimal? ApplicationFee { get; set; }

    /// <summary>VAT Stripe charged on its fees. Null on orders enriched before it was captured.</summary>
    public decimal? FeeTax { get; set; }

    /// <summary>Tax Stripe withheld from the payout. Null on orders enriched before it was captured.</summary>
    public decimal? WithheldTax { get; set; }

    /// <summary>Stored as a positive value.</summary>
    public decimal? DiscountAmount { get; set; }

    /// <summary>Standalone donation line items, VAT-exempt.</summary>
    public decimal DonationAmount { get; set; }

    /// <summary>Computed locally with the VIP split; the vendor's tax line applies 10% to the full price.</summary>
    public decimal VatAmount { get; set; }

    public ICollection<TicketAttendee> Attendees { get; set; } = new List<TicketAttendee>();
}
