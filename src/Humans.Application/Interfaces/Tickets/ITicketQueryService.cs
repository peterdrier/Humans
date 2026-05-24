using Humans.Application.DTOs;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// Query service for ticket data — checks whether a user has tickets,
/// counts matched tickets, and computes aggregate dashboard statistics.
/// All matching logic (MatchedUserId, email fallback, case-insensitive) lives here.
/// </summary>
public interface ITicketQueryService : IApplicationService
{
    /// <summary>
    /// Count tickets associated with a user as an attendee. Checks MatchedUserId
    /// on attendees first, then falls back to matching all verified user emails
    /// against attendee emails (case-insensitive). Only counts valid/checked-in attendees.
    /// A buyer who purchased tickets for others does NOT count as having a ticket.
    /// </summary>
    Task<int> GetUserTicketCountAsync(Guid userId);

    /// <summary>
    /// Get the set of user IDs that have at least one valid ticket as an
    /// attendee <b>in the active vendor event</b> (per
    /// <c>TicketSyncState.VendorEventId</c>), using MatchedUserId on attendees
    /// (valid/checked-in only). A buyer who purchased tickets for others does
    /// NOT count. Returns empty when no current vendor event is configured.
    /// Used for current-event aggregate reporting (volunteer ticket coverage,
    /// MailerLite "Humans - Has Ticket" / "Humans - Ticket no Shifts" audiences).
    /// </summary>
    Task<HashSet<Guid>> GetUserIdsWithTicketsAsync();

    /// <summary>
    /// Get all user IDs that have any ticket match (MatchedUserId set),
    /// regardless of payment or attendee status. Used for "who hasn't bought"
    /// views where any association counts.
    /// </summary>
    Task<HashSet<Guid>> GetAllMatchedUserIdsAsync();

    /// <summary>
    /// Returns the set of user ids matched to any ticket order or attendee
    /// whose purchase falls within the given calendar year (UTC). Used by the
    /// admin audience-segmentation diagnostic so it does not read the ticket
    /// tables directly (design-rules §9).
    /// </summary>
    Task<IReadOnlySet<Guid>> GetMatchedUserIdsForYearAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct calendar years (UTC, descending) in which any
    /// matched ticket order was purchased. Used by the admin
    /// audience-segmentation diagnostic to populate the year picker.
    /// </summary>
    Task<IReadOnlyList<int>> GetMatchedTicketYearsAsync(CancellationToken ct = default);

    /// <summary>
    /// Compute aggregated dashboard statistics: revenue, fees, daily sales,
    /// recent orders, volunteer coverage, and sync state.
    /// </summary>
    Task<TicketDashboardStats> GetDashboardStatsAsync();

    /// <summary>
    /// Get total gross ticket revenue (sum of TotalAmount for paid orders).
    /// Used by cash flow runway calculations.
    /// </summary>
    Task<decimal> GetGrossTicketRevenueAsync();

    /// <summary>
    /// Calculate break-even target using gross average ticket price and planned expenses.
    /// </summary>
    /// <param name="ticketsSold">Current number of tickets sold.</param>
    /// <param name="grossRevenue">Gross ticket revenue (TotalAmount sum).</param>
    /// <param name="currency">Currency code for display.</param>
    /// <param name="canAccessFinance">Whether the caller can see the finance detail breakdown.</param>
    /// <param name="fallbackTarget">Fallback break-even target from settings when calculation is not possible.</param>
    Task<BreakEvenResult> CalculateBreakEvenAsync(int ticketsSold, decimal grossRevenue, string currency, bool canAccessFinance, int fallbackTarget);

    /// <summary>
    /// Compute weekly and quarterly sales aggregates for reporting.
    /// </summary>
    Task<TicketSalesAggregates> GetSalesAggregatesAsync();

    /// <summary>
    /// Get the distinct ticket type names across all attendees, sorted alphabetically.
    /// Used for filter dropdowns on orders/attendees pages.
    /// </summary>
    Task<List<string>> GetAvailableTicketTypesAsync();

    /// <summary>
    /// Get code tracking data: campaign summaries and individual code details
    /// with redemption status. Optionally filters codes by search term.
    /// </summary>
    Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search);

    /// <summary>
    /// Get a paged list of orders with filtering and sorting.
    /// </summary>
    Task<OrdersPageResult> GetOrdersPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterPaymentStatus, string? filterTicketType, bool? filterMatched);

    /// <summary>
    /// Get a paged list of attendees with filtering and sorting.
    /// </summary>
    Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId,
        bool filterMultipleTickets = false);

    /// <summary>
    /// Get data for the "who hasn't bought" page: all active humans with ticket match status,
    /// filtered and paged.
    /// </summary>
    Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize);

    /// <summary>
    /// Get all attendees for CSV export, ordered by name.
    /// </summary>
    Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync();

    /// <summary>
    /// Get all orders for CSV export, ordered by purchase date descending.
    /// </summary>
    Task<List<OrderExportRow>> GetOrderExportDataAsync();

    /// <summary>
    /// Checks whether a user has a matched ticket record — as either an attendee
    /// or an order buyer. Used for guest dashboard and communication preferences
    /// ticketing lock.
    /// </summary>
    Task<bool> HasTicketAttendeeMatchAsync(Guid userId);

    /// <summary>
    /// Gets ticket order summaries for a specific user (as buyer), ordered by most recent first.
    /// </summary>
    Task<List<UserTicketOrderSummary>> GetUserTicketOrderSummariesAsync(Guid userId);

    /// <summary>
    /// Returns the order ids of every <see cref="TicketPaymentStatus.Paid"/> or
    /// <see cref="TicketPaymentStatus.Pending"/> ticket order matched to the user
    /// (i.e. <c>MatchedUserId == userId</c>). Refunded and Cancelled orders are
    /// excluded — the agent snapshot only counts orders the user can still act on.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOpenTicketIdsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the post-event hold date (GateOpeningDate + StrikeEndOffset + 1 day, start of day
    /// in the event's timezone) if there is an active event and the date is in the future.
    /// Returns null if no active event or the hold date has already passed.
    /// </summary>
    Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns whether a user holds a valid ticket for the current sync's vendor event.
    /// Checks paid orders matched to the user, then falls back to valid/checked-in attendees.
    /// Used by profile services to compute account-deletion / event-hold dates without
    /// touching ticket tables directly.
    /// </summary>
    Task<bool> HasCurrentEventTicketAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns ticket data for a user's GDPR data export — matched orders (as buyer)
    /// and matched attendee records. Shape matches the existing profile export JSON
    /// so GDPR exports stay stable after the service-ownership refactor.
    /// </summary>
    Task<UserTicketExportData> GetUserTicketExportDataAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct set of matched user IDs across paid ticket orders.
    /// Used by the shift coordinator dashboard to compute ticket-holder engagement
    /// counters without reading the tickets table directly.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetMatchedUserIdsForPaidOrdersAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>PurchasedAt</c> timestamp of every paid ticket order that falls
    /// within the half-open window <c>[fromInclusive, toExclusive)</c>. Used by the
    /// shift coordinator dashboard to chart daily ticket sales without reading the
    /// tickets table directly.
    /// </summary>
    Task<IReadOnlyList<Instant>> GetPaidOrderDatesInWindowAsync(
        Instant fromInclusive,
        Instant toExclusive,
        CancellationToken ct = default);

    /// <summary>
    /// Cache eviction seam invoked by <c>TicketTransferService</c> after an
    /// approved transfer has mutated local <see cref="TicketAttendee"/> rows
    /// (the voided original and, on full success, the newly issued row for the
    /// Receiver). Drops <c>UserIdsWithTickets</c>, <c>ValidAttendeeEmails</c>,
    /// and per-user ticket counts for both parties so the homepage card
    /// reflects the new state without waiting for the 5-min TTL or the next
    /// vendor sync. Pass <c>null</c> for <paramref name="receiverUserId"/>
    /// when the Receiver did not gain a local row (vendor reissue half-failed).
    /// </summary>
    void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId);

    /// <summary>
    /// Invalidates ticket-related caches after the attendee contact import
    /// has applied new matches. Drops <c>UserIdsWithTickets</c>,
    /// <c>ValidAttendeeEmails</c>, the per-event <c>TicketEventSummary</c>,
    /// and <c>TicketDashboardStats</c>. Per-user <c>UserTicketCount:{userId}</c>
    /// entries expire naturally via 5-minute TTL — same policy as the sync.
    /// </summary>
    void InvalidateAfterContactImport();

    /// <summary>
    /// Snapshot of a user's ticket holdings: count of orders where they're the
    /// buyer, plus the attendee names of every ticket where they are the
    /// current owner (per <c>TicketAttendeeOwnership</c>: matched attendee
    /// wins, falls back to order buyer for unmatched attendees).
    /// </summary>
    Task<UserTicketHoldings> GetUserTicketHoldingsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns paid orders where the number of valid+checked-in attendees is
    /// less than the total number of attendees on the order. Catches "limbo"
    /// states from any cause (failed transfer reissue, manual TT-dashboard
    /// edits without resync, refunds, etc.).
    /// </summary>
    Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default);
}

/// <summary>
/// Ticket data for a user's GDPR data export.
/// </summary>
public record UserTicketExportData(
    IReadOnlyList<UserTicketOrderExportRow> Orders,
    IReadOnlyList<UserTicketAttendeeExportRow> Attendees);

/// <summary>
/// A single ticket order row in the user data export.
/// </summary>
public record UserTicketOrderExportRow(
    string? BuyerName,
    string? BuyerEmail,
    decimal TotalAmount,
    string Currency,
    string PaymentStatus,
    string? DiscountCode,
    Instant PurchasedAt);

/// <summary>
/// A single ticket attendee row in the user data export.
/// </summary>
public record UserTicketAttendeeExportRow(
    string? AttendeeName,
    string? AttendeeEmail,
    string? TicketTypeName,
    decimal Price,
    string Status);

/// <summary>
/// Summary of a ticket order for display on user-facing pages.
/// </summary>
public record UserTicketOrderSummary(
    string BuyerName,
    Instant PurchasedAt,
    int AttendeeCount,
    decimal TotalAmount,
    string Currency);

/// <summary>
/// Holdings summary for a single user — orders they bought + per-ticket rows
/// for every ticket they currently hold (per the ownership cascade). Includes
/// voided tickets so the UI can show them distinctly rather than hide them.
/// Used by the &lt;vc:ticket-holdings&gt; profile sidebar.
/// </summary>
public record UserTicketHoldings(
    int OrderCount,
    IReadOnlyList<UserTicketHoldingRow> Tickets);

/// <summary>
/// One ticket held by a user, with enough info for the holdings widget to
/// render name, type, and status distinctly (voided tickets are kept in the
/// list so they're visible but visually muted).
/// </summary>
public record UserTicketHoldingRow(
    Guid AttendeeId,
    string AttendeeName,
    string? AttendeeEmail,
    string VendorTicketId,
    string TicketTypeName,
    Humans.Domain.Enums.TicketAttendeeStatus Status);

/// <summary>
/// A single row in the order-drift diagnostic: a paid order whose live
/// (Valid or CheckedIn) attendee count is less than its total attendee count.
/// </summary>
public sealed record OrderDriftRow(
    Guid OrderId,
    string VendorOrderId,
    string BuyerName,
    int IssuedCount,
    int ValidCount,
    string? VendorDashboardUrl);
