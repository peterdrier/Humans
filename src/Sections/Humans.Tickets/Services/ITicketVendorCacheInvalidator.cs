namespace Humans.Tickets.Services;

/// <summary>
/// One-way cache-staleness signal for the vendor event-summary cache. Implemented
/// by the Singleton caching decorator that wraps <see cref="Contracts.ITicketVendorService"/>
/// (<see cref="Stores.CachingTicketVendorService"/>).
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ITicketCacheInvalidator"/> — the vendor event summary is
/// the vendor decorator's own cache, not the query decorator's, and an architecture test
/// pins <see cref="ITicketCacheInvalidator"/> to a single implementer
/// (<see cref="Stores.CachingTicketQueryService"/>), so it cannot also front this seam.
/// <c>TicketSyncService</c> is the sole caller, injecting this interface directly.
/// </remarks>
internal interface ITicketVendorCacheInvalidator
{
    /// <summary>Drops the cached vendor event summary keyed on <paramref name="vendorEventId"/>.</summary>
    void InvalidateEventSummary(string vendorEventId);
}
