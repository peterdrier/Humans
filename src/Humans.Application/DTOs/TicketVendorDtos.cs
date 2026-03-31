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
public record VendorTicketDto(
    string VendorTicketId,
    string VendorOrderId,
    string AttendeeName,
    string? AttendeeEmail,
    string TicketTypeName,
    decimal Price,
    string Status);

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
