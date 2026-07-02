using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application;

/// <summary>
/// Compact projection of a <see cref="Humans.Domain.Entities.TicketAttendee"/>
/// row carried inside <see cref="TicketOrderInfo"/>. One per issued ticket.
/// <paramref name="CheckedInAt"/> is the vendor check-in signal (from the
/// <c>/check_ins</c> sync) — the issued ticket's <paramref name="Status"/> stays
/// <c>Valid</c> when checked in, so consumers must test <paramref name="CheckedInAt"/>.
/// </summary>
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
