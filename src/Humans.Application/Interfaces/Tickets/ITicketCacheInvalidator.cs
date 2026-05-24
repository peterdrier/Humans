namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// One-way cache-staleness signal for the Tickets section's read model.
/// Implemented by the Singleton caching decorator that wraps
/// <see cref="ITicketService"/>. Write-side callers poke this seam after
/// committing changes instead of reaching into the decorator's private cache
/// layout.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ITicketServiceRead"/> so the budgeted read
/// surface does not grow each time a new invalidation seam is needed, and so
/// callers that only invalidate do not depend on the full query service.
/// </remarks>
public interface ITicketCacheInvalidator
{
    /// <summary>
    /// Cache eviction seam invoked after an approved transfer has mutated local
    /// ticket rows. Drops the order projection and per-user entries for both
    /// affected users. Pass null for <paramref name="receiverUserId"/> when
    /// the receiver did not gain a local row.
    /// </summary>
    void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId);

    /// <summary>
    /// Invalidates ticket-related caches after the attendee contact import has
    /// applied new matches.
    /// </summary>
    void InvalidateAfterContactImport();

    /// <summary>
    /// Signals that broad ticket data changed. Drops the order projection and
    /// per-user holdings cache; the next read re-warms from the keyed inner
    /// ticket service.
    /// </summary>
    void InvalidateAll();

    /// <summary>
    /// Drops per-user entries for both users affected by an account-merge fold.
    /// </summary>
    void InvalidateAfterUserMerge(Guid sourceUserId, Guid targetUserId);

    /// <summary>
    /// Drops the cached vendor event summary keyed on
    /// <paramref name="vendorEventId"/>.
    /// </summary>
    void InvalidateVendorEventSummary(string vendorEventId);
}
