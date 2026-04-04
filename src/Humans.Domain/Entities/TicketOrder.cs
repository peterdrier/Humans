using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A ticket purchase order synced from the ticket vendor.
/// One record per purchase — may contain multiple attendees (issued tickets).
/// </summary>
public class TicketOrder
{
    public Guid Id { get; init; }

    /// <summary>Vendor's order identifier (e.g. TT order ID). Unique.</summary>
    public string VendorOrderId { get; init; } = string.Empty;

    /// <summary>Buyer's name from the vendor.</summary>
    public string BuyerName { get; set; } = string.Empty;

    /// <summary>Buyer's email from the vendor.</summary>
    public string BuyerEmail { get; set; } = string.Empty;

    /// <summary>Auto-matched user by email. Null if no match found.</summary>
    public Guid? MatchedUserId { get; set; }

    /// <summary>Navigation to matched user.</summary>
    public User? MatchedUser { get; set; }

    /// <summary>Order total amount.</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>Currency code (e.g. "EUR").</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Discount/voucher code used, if any.</summary>
    public string? DiscountCode { get; set; }

    /// <summary>Payment status from vendor.</summary>
    public TicketPaymentStatus PaymentStatus { get; set; }

    /// <summary>Vendor event ID at time of sync (for future multi-event).</summary>
    public string VendorEventId { get; set; } = string.Empty;

    /// <summary>Deep link to vendor dashboard for this order.</summary>
    public string? VendorDashboardUrl { get; set; }

    /// <summary>When the purchase was made (from vendor data).</summary>
    public Instant PurchasedAt { get; set; }

    /// <summary>When this record was last synced from the vendor.</summary>
    public Instant SyncedAt { get; set; }

    /// <summary>Stripe PaymentIntent ID from vendor txn_id (e.g. pi_xxx). Used to look up fee details.</summary>
    public string? StripePaymentIntentId { get; set; }

    /// <summary>Payment method type from Stripe (e.g. "card", "link", "ideal", "bancontact", "klarna").</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>Payment method detail — card brand for cards (e.g. "visa", "mastercard"), null for others.</summary>
    public string? PaymentMethodDetail { get; set; }

    /// <summary>Stripe processing fee in order currency.</summary>
    public decimal? StripeFee { get; set; }

    /// <summary>Application fee (Ticket Tailor platform fee) in order currency.</summary>
    public decimal? ApplicationFee { get; set; }

    /// <summary>Discount amount applied to order (from vendor line items). Stored as positive value.</summary>
    public decimal? DiscountAmount { get; set; }

    /// <summary>Standalone donation amount from vendor line items (VAT-exempt). Stored in euros.</summary>
    public decimal DonationAmount { get; set; }

    /// <summary>Correctly computed VAT amount using VIP split logic. Not from vendor (TT computes incorrectly).</summary>
    public decimal VatAmount { get; set; }

    /// <summary>Individual attendees (issued tickets) for this order.</summary>
    public ICollection<TicketAttendee> Attendees { get; set; } = new List<TicketAttendee>();
}
