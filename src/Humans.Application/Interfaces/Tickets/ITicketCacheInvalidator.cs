namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// One-way cache-staleness signal for the Tickets section. Implemented by the
/// Singleton caching decorator that wraps <see cref="ITicketQueryService"/>.
/// Write-side callers poke this seam after committing changes instead of
/// reaching into the decorator's private cache layout.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ITicketQueryService"/> so the query surface
/// does not grow each time a new invalidation seam is needed, and so callers
/// that only invalidate do not depend on the full query service.
/// </remarks>
public interface ITicketCacheInvalidator
{
    /// <summary>
    /// Signals that broad ticket data changed. Per-user short-TTL entries
    /// expire naturally via their TTL.
    /// </summary>
    void InvalidateAll();

    /// <summary>
    /// Drops per-user short-TTL entries for both users affected by an
    /// account-merge fold.
    /// </summary>
    void InvalidateAfterUserMerge(Guid sourceUserId, Guid targetUserId);

    /// <summary>
    /// Drops the cached vendor event summary keyed on
    /// <paramref name="vendorEventId"/>.
    /// </summary>
    void InvalidateVendorEventSummary(string vendorEventId);
}
