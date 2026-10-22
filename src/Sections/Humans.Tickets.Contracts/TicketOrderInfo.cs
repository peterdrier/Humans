using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>A gate scan keeps <paramref name="Status"/> at Valid: test <paramref name="CheckedInAt"/> for check-in.</summary>
public sealed record TicketAttendeeInfo(
    Guid Id,
    string VendorTicketId,
    string? AttendeeName,
    string? AttendeeEmail,
    string? TicketTypeName,
    decimal Price,
    TicketAttendeeStatus Status,
    Guid? MatchedUserId,
    string? Barcode = null,
    string? TransferredToName = null,
    Instant? TransferredAt = null,
    Instant? CheckedInAt = null);

/// <summary>
/// <paramref name="FeeTax"/> (VAT on Stripe's fees) and <paramref name="WithheldTax"/> are null on orders enriched
/// before those Stripe buckets were captured.
/// </summary>
public sealed record TicketOrderInfo(
    Guid Id,
    string VendorOrderId,
    string? BuyerName,
    string? BuyerEmail,
    decimal TotalAmount,
    string Currency,
    string? DiscountCode,
    TicketPaymentStatus PaymentStatus,
    string VendorEventId,
    Instant PurchasedAt,
    Guid? MatchedUserId,
    bool IsCurrentEvent,
    IReadOnlyList<TicketAttendeeInfo> Attendees,
    decimal? StripeFee = null,
    decimal? ApplicationFee = null,
    decimal? FeeTax = null,
    decimal? WithheldTax = null)
{
    /// <summary>
    /// Matches a configured vendor event exactly, or the current event when no event is configured.
    /// </summary>
    public bool IsForEvent(string? vendorEventId) =>
        vendorEventId is not null
            ? string.Equals(VendorEventId, vendorEventId, StringComparison.Ordinal)
            : IsCurrentEvent;
}
