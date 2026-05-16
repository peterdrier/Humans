namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// One-way cache-staleness signal for the Tickets section's read model.
/// Implemented by the Singleton caching decorator that wraps
/// <see cref="ITicketQueryService"/>. Every Tickets-section internal call
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
/// Kept separate from <see cref="ITicketQueryService"/> so the budgeted
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
}
