using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Application.Interfaces;

/// <summary>
/// Query service for ticket data — checks whether a user has tickets,
/// counts matched tickets, and computes aggregate dashboard statistics.
/// All matching logic (MatchedUserId, email fallback, case-insensitive) lives here.
/// </summary>
public interface ITicketQueryService
{
    /// <summary>
    /// Count tickets associated with a user as an attendee. Checks MatchedUserId
    /// on attendees first, then falls back to matching all verified user emails
    /// against attendee emails (case-insensitive). Only counts valid/checked-in attendees.
    /// A buyer who purchased tickets for others does NOT count as having a ticket.
    /// </summary>
    Task<int> GetUserTicketCountAsync(Guid userId);

    /// <summary>
    /// Get the set of user IDs that have at least one valid ticket as an attendee,
    /// using MatchedUserId on attendees (valid/checked-in only).
    /// A buyer who purchased tickets for others does NOT count.
    /// Used for aggregate reporting like volunteer ticket coverage.
    /// </summary>
    Task<HashSet<Guid>> GetUserIdsWithTicketsAsync();

    /// <summary>
    /// Get all user IDs that have any ticket match (MatchedUserId set),
    /// regardless of payment or attendee status. Used for "who hasn't bought"
    /// views where any association counts.
    /// </summary>
    Task<HashSet<Guid>> GetAllMatchedUserIdsAsync();

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
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId);

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
