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

public static class TicketOrderInfoExtensions
{
    public static IReadOnlySet<Guid> CurrentEventTicketHolderUserIds(
        this IEnumerable<TicketOrderInfo> orders) =>
        orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .Where(a => a.MatchedUserId.HasValue && IsValidOrCheckedIn(a.Status))
            .Select(a => a.MatchedUserId!.Value)
            .ToHashSet();

    public static IReadOnlySet<Guid> AllMatchedUserIds(
        this IEnumerable<TicketOrderInfo> orders) =>
        orders
            .SelectMany(o => o.MatchedUserId.HasValue
                ? o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value)
                    .Append(o.MatchedUserId.Value)
                : o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value))
            .ToHashSet();

    public static IReadOnlySet<Guid> MatchedUserIdsForYear(
        this IEnumerable<TicketOrderInfo> orders,
        int year)
    {
        var start = Instant.FromUtc(year, 1, 1, 0, 0);
        var end = Instant.FromUtc(year + 1, 1, 1, 0, 0);
        return orders
            .Where(o => o.PurchasedAt >= start && o.PurchasedAt < end)
            .AllMatchedUserIds();
    }

    public static IReadOnlyList<int> MatchedTicketYears(
        this IEnumerable<TicketOrderInfo> orders) =>
        orders
            .Where(o => o.MatchedUserId.HasValue)
            .Select(o => o.PurchasedAt.InUtc().Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

    public static decimal GrossPaidRevenue(this IEnumerable<TicketOrderInfo> orders) =>
        orders
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .Sum(o => o.TotalAmount);

    public static IReadOnlyCollection<Guid> MatchedBuyerUserIdsForPaidOrders(
        this IEnumerable<TicketOrderInfo> orders) =>
        orders
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && o.MatchedUserId.HasValue)
            .Select(o => o.MatchedUserId!.Value)
            .Distinct()
            .ToList();

    public static IReadOnlyList<Instant> PaidOrderDatesInWindow(
        this IEnumerable<TicketOrderInfo> orders,
        Instant fromInclusive,
        Instant toExclusive) =>
        orders
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid
                && o.PurchasedAt >= fromInclusive
                && o.PurchasedAt < toExclusive)
            .Select(o => o.PurchasedAt)
            .ToList();

    private static bool IsValidOrCheckedIn(TicketAttendeeStatus status) =>
        status == TicketAttendeeStatus.Valid || status == TicketAttendeeStatus.CheckedIn;
}
