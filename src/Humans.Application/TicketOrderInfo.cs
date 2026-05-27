using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application;

/// <summary>
/// Compact projection of a <see cref="Humans.Domain.Entities.TicketAttendee"/>
/// row carried inside <see cref="TicketOrderInfo"/>. One per issued ticket.
/// </summary>
public sealed record TicketAttendeeInfo(
    Guid Id,
    string VendorTicketId,
    string? AttendeeName,
    string? AttendeeEmail,
    string? TicketTypeName,
    decimal Price,
    TicketAttendeeStatus Status,
    Guid? MatchedUserId);

/// <summary>
/// Compact projection of a <see cref="Humans.Domain.Entities.TicketOrder"/>
/// row, with embedded <see cref="TicketAttendeeInfo"/> children. Used as the
/// canonical in-memory shape behind <c>CachingTicketQueryService</c>'s
/// order projection.
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
    decimal? ApplicationFee = null);
