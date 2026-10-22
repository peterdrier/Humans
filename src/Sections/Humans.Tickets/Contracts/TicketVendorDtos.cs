using NodaTime;

namespace Humans.Tickets.Contracts;

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
    string? StripePaymentIntentId = null,
    decimal? DiscountAmount = null,
    decimal DonationAmount = 0m);

public record VendorTicketDto(
    string VendorTicketId,
    string? VendorOrderId,
    string AttendeeName,
    string? AttendeeEmail,
    string TicketTypeName,
    decimal Price,
    string Status,
    string? Barcode = null);

/// <summary>Separate from the ticket's status, which stays valid once scanned.</summary>
public record VendorCheckInDto(string VendorTicketId, Instant CheckedInAt);

public record VendorEventSummaryDto(
    string EventId,
    string EventName,
    int TotalCapacity,
    int TicketsSold,
    int TicketsRemaining);

public record DiscountCodeSpec(
    int Count,
    DiscountType DiscountType,
    decimal DiscountValue,
    Instant? ExpiresAt);

public enum DiscountType
{
    Percentage,
    Fixed
}

/// <summary><see cref="HoldId"/> is set when voided to a hold: reissuing against it works even if the ticket type
/// is now closed or sold out.</summary>
public sealed record VoidIssuedTicketResult(string VendorTicketId, string? HoldId);

/// <summary>
/// Supply either <see cref="HoldId"/> or <see cref="EventId"/>+<see cref="TicketTypeId"/>. The vendor links
/// API-issued tickets to no order, so pass the transfer request id as <see cref="ExternalReference"/> for the next
/// sync to re-link.
/// </summary>
public sealed record IssueTicketRequest(
    string? EventId,
    string? TicketTypeId,
    string? HoldId,
    string FullName,
    string? Email,
    bool SendEmail,
    string? ExternalReference);

public sealed class TicketVendorWriteException : Exception
{
    public TicketVendorWriteException() { }
    public TicketVendorWriteException(string message) : base(message) { }
    public TicketVendorWriteException(string message, Exception inner) : base(message, inner) { }

    public TicketVendorWriteException(string message, TicketVendorFailureKind kind, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public TicketVendorFailureKind Kind { get; }
}

public enum TicketVendorFailureKind
{
    /// <summary>HTTP 400 / 422 — bad payload, sold out, seated ticket type. Do not retry.</summary>
    Validation,
    /// <summary>HTTP 401 / 403 — credential rotation problem. Do not retry.</summary>
    AuthFailed,
    /// <summary>HTTP 404 — ticket already voided or unknown. Treat per-call.</summary>
    NotFound,
    /// <summary>HTTP 429 — rate limited. Surface to user; do not auto-retry mid-request.</summary>
    RateLimited,
    /// <summary>HTTP 5xx or transport failure. May retry from the admin UI.</summary>
    Transient,
}
