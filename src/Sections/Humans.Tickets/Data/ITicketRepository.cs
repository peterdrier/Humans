using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using NodaTime;
using Humans.Base.Interfaces.Repositories;
using Humans.Tickets.Domain;
using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Data;

/// <summary>
/// Every method uses its own short-lived context, so returned entities are detached: mutate them and pass them back
/// to the matching write.
/// </summary>
internal interface ITicketRepository : IRepository
{
    /// <summary>Null when the seed row is missing: a configuration error.</summary>
    Task<TicketSyncState?> GetSyncStateAsync(CancellationToken ct = default);

    /// <summary>Inserts the row when it doesn't exist yet.</summary>
    Task PersistSyncStateAsync(TicketSyncState state, CancellationToken ct = default);

    /// <summary>No-op when the row doesn't exist.</summary>
    Task ResetSyncStateLastSyncAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, TicketOrder>> GetOrdersByVendorIdsAsync(
        IReadOnlyCollection<string> vendorOrderIds,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, TicketAttendee>> GetAttendeesByVendorIdsAsync(
        IReadOnlyCollection<string> vendorTicketIds,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, Guid>> GetOrderIdsByVendorIdAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<TicketOrder>> GetOrdersNeedingStripeEnrichmentAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<TicketOrder>> GetAllOrdersWithAttendeesAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<OrderDiscountCodeRow>> GetOrderDiscountCodesAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Includes the parent order but not its sibling attendees: that cycle fails under AsNoTracking. Use
    /// <see cref="GetVendorTicketIdsForOrderAsync"/> for siblings.
    /// </summary>
    Task<TicketAttendee?> GetAttendeeByIdAsync(Guid attendeeId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetVendorTicketIdsForOrderAsync(
        Guid orderId, CancellationToken ct = default);

    /// <summary>As buyer or as matched attendee, so tickets transferred in are included.</summary>
    Task<IReadOnlyList<TicketAttendee>> GetAttendeesVisibleToUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<MatchedAttendeeRow>> GetMatchedAttendeesForEventAsync(
        string vendorEventId,
        CancellationToken ct = default);

    Task UpsertOrdersAsync(IReadOnlyList<TicketOrder> orders, CancellationToken ct = default);

    Task UpsertAttendeesAsync(IReadOnlyList<TicketAttendee> attendees, CancellationToken ct = default);

    /// <summary>
    /// <see cref="TicketAttendee.CheckedInAt"/> is write-once (earliest scan wins, never cleared) and <c>Status</c> is
    /// left alone. Check-ins for tickets not yet synced are skipped.
    /// </summary>
    Task ApplyCheckInsAsync(IReadOnlyList<VendorCheckInDto> checkIns, CancellationToken ct = default);

    /// <summary>Unmatched, <c>Valid</c> or <c>CheckedIn</c>, with a non-empty email.</summary>
    Task<IReadOnlyList<TicketAttendee>> GetUnmatchedActiveAttendeesAsync(
        string vendorEventId, CancellationToken ct = default);

    /// <summary>Writes only <c>VatAmount</c>.</summary>
    Task UpdateOrderVatAmountsAsync(
        IReadOnlyList<(Guid OrderId, decimal VatAmount)> updates,
        CancellationToken ct = default);

    Task UpdateOrderStripeEnrichmentAsync(
        IReadOnlyList<TicketOrder> orders,
        CancellationToken ct = default);

    Task<int> CountValidAttendeesMatchedToUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Attendees in <c>Valid</c> or <c>CheckedIn</c> state, nulls excluded.</summary>
    Task<IReadOnlyList<string>> GetValidAttendeeEmailsAsync(CancellationToken ct = default);

    /// <summary>The set form of <see cref="HasEventTicketAsync"/>.</summary>
    Task<IReadOnlyList<Guid>> GetEventTicketHolderUserIdsAsync(
        string vendorEventId, CancellationToken ct = default);

    /// <summary>
    /// Matched to a <c>Valid</c> or <c>CheckedIn</c> attendee row for the event. A buyer-only match is not a holding.
    /// </summary>
    Task<bool> HasEventTicketAsync(Guid userId, string vendorEventId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetDistinctTicketTypesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TicketOrder>> GetOrdersMatchedToUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketAttendee>> GetAttendeesMatchedToUserAsync(Guid userId, CancellationToken ct = default);

    Task<TicketDashboardTotals> GetDashboardTotalsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PaidOrderPaymentMethodRow>> GetPaidOrderPaymentMethodsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OrderDateAndCount>> GetOrderDateAttendeeCountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RecentOrder>> GetRecentOrdersAsync(int count, CancellationToken ct = default);

    Task<IReadOnlyList<PaidOrderSalesRow>> GetPaidOrderSalesRowsAsync(CancellationToken ct = default);

    /// <summary>Gross and processing fees, which a refund doesn't return.</summary>
    Task<IReadOnlyList<RefundedOrderRow>> GetRefundedOrderRowsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PaidAttendeeTypePriceRow>> GetPaidAttendeeTypePriceRowsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DiscountCodeOrderRow>> GetOrdersWithDiscountCodesAsync(CancellationToken ct = default);

    Task<(IReadOnlyList<OrderRow> Rows, int TotalCount)> GetOrdersPageAsync(
        string? search,
        string sortBy,
        bool sortDesc,
        int page,
        int pageSize,
        TicketPaymentStatus? filterPaymentStatus,
        string? filterTicketType,
        bool? filterMatched,
        CancellationToken ct = default);

    Task<(IReadOnlyList<AttendeeRow> Rows, int TotalCount)> GetAttendeesPageAsync(
        string? search,
        string sortBy,
        bool sortDesc,
        int page,
        int pageSize,
        string? filterTicketType,
        TicketAttendeeStatus? filterStatus,
        bool? filterMatched,
        string? filterOrderId,
        bool filterMultipleTickets,
        CancellationToken ct = default);

    Task<IReadOnlyList<AttendeeExportRow>> GetAttendeeExportDataAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OrderExportRow>> GetOrderExportDataAsync(CancellationToken ct = default);

    Task<TicketSyncState?> ResetStaleRunningStateAsync(
        Instant olderThan,
        Instant now,
        string errorMessage,
        CancellationToken ct = default);

    /// <summary>Paid orders with fewer live (Valid or CheckedIn) tickets than attendees.</summary>
    Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default);

    /// <summary>Plain re-FK: tickets are unique per purchase. <paramref name="updatedAt"/> is unused.</summary>
    Task ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Tombstones buyer and attendee name and email on the user's rows and drops <c>MatchedUserId</c>. The rows stay
    /// as sales records.
    /// </summary>
    Task<int> EraseUserPiiAsync(Guid userId, CancellationToken ct = default);
}
