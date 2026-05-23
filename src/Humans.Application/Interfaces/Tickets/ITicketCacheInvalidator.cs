namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// One-way cache-staleness signal for the Tickets section's read model.
/// Implemented by the Singleton caching decorator that wraps
/// <see cref="ITicketService"/>. Every Tickets-section internal call
/// site whose write surface is too narrow to flow through the decorator
/// (the sync job, the account-merge fold, the attendee contact import)
/// pokes the cache through this seam after committing its own writes.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>IShiftViewInvalidator</c> (Shifts §15) and
/// <c>IActiveTeamsCacheInvalidator</c> (Teams §15). The decorator drops
/// its projection; the next read re-warms from <see cref="Repositories.ITicketRepository"/>.
/// Consumers never poke <c>IMemoryCache</c> for ticket keys directly — the
/// section-internal cache layout is private to the decorator.
/// </para>
///
/// <para>
/// Kept separate from <see cref="ITicketServiceRead"/> so the budgeted
/// query surface doesn't grow each time a new invalidation seam is needed,
/// and so callers that only invalidate (and don't read) don't depend on
/// the full query surface. Mirrors how the Shifts section keeps reads
/// (<c>IShiftView</c>) and write-side eviction (<c>IShiftViewInvalidator</c>)
/// on separate interfaces.
/// </para>
/// </remarks>
public interface ITicketCacheInvalidator
{
    /// <summary>
    /// Cache eviction seam invoked after an approved transfer has mutated local
    /// ticket rows. Drops the projection and per-user entries for both affected
    /// users. Pass null for <paramref name="receiverUserId"/> when the receiver
    /// did not gain a local row.
    /// </summary>
    void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId);

    /// <summary>
    /// Invalidates ticket-related caches after the attendee contact import has
    /// applied new matches.
    /// </summary>
    void InvalidateAfterContactImport();

    /// <summary>
    /// Drops the entire ticket projection (orders + attendees + derived
    /// aggregate views). Per-user short-TTL entries expire naturally via
    /// their TTL — same policy as the contact-import path.
    /// </summary>
    /// <remarks>
    /// Called by <c>TicketSyncService</c> after a successful vendor sync.
    /// The sync upserts every order and attendee that changed since the
    /// last cursor, so the full projection must re-warm on the next read.
    /// </remarks>
    void InvalidateAll();

    /// <summary>
    /// Drops the entire ticket projection AND the per-user short-TTL
    /// entries for both users affected by an account-merge fold. The
    /// merge re-FKs orders + attendees from <paramref name="sourceUserId"/>
    /// to <paramref name="targetUserId"/>; both users' per-user entries
    /// must be evicted or the homepage card and ticket-holdings widget
    /// lag up to 5 minutes after the merge. Called by
    /// <c>TicketSyncService.ReassignAsync</c> (the section's
    /// <c>IUserMerge</c> implementation).
    /// </summary>
    void InvalidateAfterUserMerge(Guid sourceUserId, Guid targetUserId);

    /// <summary>
    /// Drops the cached vendor event summary keyed on
    /// <paramref name="vendorEventId"/>. The summary lives on the
    /// connector's <c>IMemoryCache</c> (Infrastructure) and is keyed by
    /// the vendor event id; the sync service has no business holding
    /// <c>IMemoryCache</c> itself (design-rules §15c — Application is
    /// cache-unaware), so it pokes through this seam after a successful
    /// sync. The decorator owns the eviction call to <c>IMemoryCache</c>.
    /// </summary>
    void InvalidateVendorEventSummary(string vendorEventId);
}
