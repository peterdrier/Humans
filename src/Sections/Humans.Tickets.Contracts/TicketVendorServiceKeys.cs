namespace Humans.Tickets.Contracts;

/// <summary>
/// DI key for the vendor port's keyed inner. Lives on the leaf (rather than beside
/// <c>ITicketVendorService</c> itself, in <c>Humans.Tickets/Contracts/</c>) so the one vendor
/// adapter section (<c>Humans.TicketTailor</c>) can bind its keyed live/stub pair under this
/// key without seeing Tickets' internals — no cross-section <c>InternalsVisibleTo</c> needed
/// to name Tickets' own caching decorator, which resolves the same keyed inner independently.
/// </summary>
public static class TicketVendorServiceKeys
{
    /// <summary>
    /// Key for the keyed-Scoped <c>ITicketVendorService</c> inner that
    /// <c>CachingTicketVendorService</c> (Tickets) wraps and the vendor adapter
    /// section registers.
    /// </summary>
    public const string InnerServiceKey = "ticket-vendor-inner";
}
