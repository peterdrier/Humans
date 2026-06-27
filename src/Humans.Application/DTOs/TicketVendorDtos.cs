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
    Instant? CheckedInAt = null,
    string? Barcode = null);

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

// ── Vendor write surface (ticket transfer void+reissue) ──────────────────────

/// <summary>Outcome of a vendor void call. <see cref="HoldId"/> is non-null when the
/// ticket was voided <em>to a hold</em> — the seat's allocation is reserved off-sale so a
/// subsequent <see cref="IssueTicketRequest"/> against that hold reissues the same ticket
/// type even if it is now closed/sold out.</summary>
public sealed record VoidIssuedTicketResult(string VendorTicketId, string? HoldId);

/// <summary>
/// Payload for a vendor issue-ticket call. Supply EITHER <see cref="HoldId"/> (reissue the
/// reserved allocation from a prior void-to-hold — the like-for-like transfer path) OR
/// <see cref="EventId"/>+<see cref="TicketTypeId"/> (issue fresh inventory). TicketTailor
/// does NOT associate API-issued tickets with an order (the new ticket has
/// <c>order_id=null</c>, <c>source="api"</c>); pass the Humans transfer request id as
/// <see cref="ExternalReference"/> so the next sync re-links the orphan to the original order.
/// </summary>
public sealed record IssueTicketRequest(
    string? EventId,
    string? TicketTypeId,
    string? HoldId,
    string FullName,
    string? Email,
    bool SendEmail,
    string? ExternalReference);

/// <summary>Categorised vendor write failure, raised by the vendor client so the transfer
/// service can decide between manual fallback and partial-state handling.</summary>
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

/// <summary>Classification of a <see cref="TicketVendorWriteException"/> by HTTP status.</summary>
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
