namespace Humans.Application.Interfaces;

/// <summary>
/// Query service for ticket data — checks whether a user has tickets,
/// counts matched tickets, etc. All matching logic (MatchedUserId,
/// email fallback, case-insensitive) lives here.
/// </summary>
public interface ITicketQueryService
{
    /// <summary>
    /// Count tickets associated with a user. Checks MatchedUserId on both
    /// orders and attendees, then falls back to matching all verified user
    /// emails against buyer/attendee emails (case-insensitive).
    /// Only counts paid orders and valid/checked-in attendees.
    /// </summary>
    Task<int> GetUserTicketCountAsync(Guid userId);

    /// <summary>
    /// Get the set of user IDs that have at least one valid ticket,
    /// using MatchedUserId on orders (paid only) and attendees (valid/checked-in).
    /// Used for aggregate reporting like volunteer ticket coverage.
    /// </summary>
    Task<HashSet<Guid>> GetUserIdsWithTicketsAsync();
}
