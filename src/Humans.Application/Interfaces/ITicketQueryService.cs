using Humans.Application.DTOs;

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
}
