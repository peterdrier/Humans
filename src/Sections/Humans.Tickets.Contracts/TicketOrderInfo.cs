using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>A gate scan keeps <paramref name="Status"/> at Valid: test <paramref name="CheckedInAt"/> for check-in.</summary>
public sealed record TicketAttendeeInfo(
    Guid Id,
    string? AttendeeName,
    string? AttendeeEmail,
    string? TicketTypeName,
    TicketAttendeeStatus Status,
    Guid? MatchedUserId,
    string? Barcode = null,
    string? TransferredToName = null,
    Instant? TransferredAt = null,
    Instant? CheckedInAt = null);

/// <summary>
/// <paramref name="FeeTax"/> (VAT on Stripe's fees) is null on orders enriched before that bucket was captured.
/// </summary>
public sealed record TicketOrderInfo(
    Guid Id,
    string? BuyerEmail,
    decimal TotalAmount,
    TicketPaymentStatus PaymentStatus,
    string VendorEventId,
    Instant PurchasedAt,
    Guid? MatchedUserId,
    bool IsCurrentEvent,
    IReadOnlyList<TicketAttendeeInfo> Attendees,
    decimal? StripeFee = null,
    decimal? ApplicationFee = null,
    decimal? FeeTax = null)
{
    /// <summary>
    /// Matches a configured vendor event exactly, or the current event when no event is configured.
    /// With <paramref name="paidOnly"/>, only paid orders count toward revenue and ticketing actuals.
    /// </summary>
    public bool IsForEvent(string? vendorEventId, bool paidOnly = false) =>
        (!paidOnly || PaymentStatus == TicketPaymentStatus.Paid)
        && (vendorEventId is not null
            ? string.Equals(VendorEventId, vendorEventId, StringComparison.Ordinal)
            : IsCurrentEvent);
}
