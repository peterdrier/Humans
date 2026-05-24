using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application;

/// <summary>
/// Compact projection of a <see cref="Humans.Domain.Entities.TicketAttendee"/>
/// row carried inside <see cref="TicketOrderInfo"/>. One per issued ticket.
/// </summary>
/// <remarks>
/// Only fields actually consumed by the cached read paths
/// (<c>GetUserIdsWithTicketsAsync</c>, <c>GetUserTicketHoldingsAsync</c>,
/// per-user ticket-count probes, GDPR contributor) are projected. Vendor-only
/// fields and synthetics computed from order context stay on the entity.
/// </remarks>
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
/// projection — keyed by <see cref="Id"/>, populated once at warm time and
/// refreshed wholesale on every section-level invalidation event (vendor
/// sync, transfer approve, contact import apply, account-merge fold).
/// </summary>
/// <remarks>
/// <para>
/// Per-order keyed dict with attendees embedded — the spec for T-07. This
/// shape supports every cached read in the Tickets section: user-with-ticket
/// sets, per-user count, per-user holdings, matched-user-ids in a window,
/// paid-order dates in a window, vendor-event ticket probes, distinct match
/// years.
/// </para>
/// <para>
/// At ~500 users / ~450 orders / ~600 attendees the full projection sits well
/// under the 50 MB / section budget (each record is ~200 bytes; expect &lt;1 MB).
/// </para>
/// </remarks>
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
    IReadOnlyList<TicketAttendeeInfo> Attendees);
