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
}
