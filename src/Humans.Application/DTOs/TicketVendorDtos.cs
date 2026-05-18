using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>Vendor-agnostic order data returned by ITicketVendorService.</summary>
public record VendorOrderDto(
    string VendorOrderId,
    string BuyerName,
    string BuyerEmail,
    decimal TotalAmount,
    string Currency,
    string? DiscountCode,
    string PaymentStatus,
    string? VendorDashboardUrl,
    Instant PurchasedAt,
    IReadOnlyList<VendorTicketDto> Tickets,
    string? StripePaymentIntentId = null,
    decimal? DiscountAmount = null,
    decimal DonationAmount = 0m);

/// <summary>Vendor-agnostic issued ticket data.</summary>
/// <param name="CheckedInAt">
/// When the attendee was checked in at the gate, when the vendor exposes it
/// (TicketTailor: <c>check_in.checked_in_at</c>, epoch seconds). Null when the
/// vendor did not return a timestamp or the attendee is not checked in. Issue
/// nobodies-collective/Humans#736.
/// </param>
public record VendorTicketDto(
    string VendorTicketId,
    string? VendorOrderId,
    string AttendeeName,
    string? AttendeeEmail,
    string TicketTypeName,
    decimal Price,
    string Status,
    Instant? CheckedInAt = null);

/// <summary>High-level event summary from vendor.</summary>
public record VendorEventSummaryDto(
    string EventId,
    string EventName,
    int TotalCapacity,
    int TicketsSold,
    int TicketsRemaining);

/// <summary>Specification for generating discount codes via vendor API.</summary>
public record DiscountCodeSpec(
    int Count,
    DiscountType DiscountType,
    decimal DiscountValue,
    Instant? ExpiresAt);

/// <summary>Type of discount for code generation.</summary>
public enum DiscountType
{
    Percentage,
    Fixed
}

/// <summary>Redemption status of a discount code from the vendor.</summary>
public record DiscountCodeStatusDto(
    string Code,
    bool IsRedeemed,
    int TimesUsed);
