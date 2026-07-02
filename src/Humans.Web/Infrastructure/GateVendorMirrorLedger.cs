using Microsoft.Extensions.Caching.Memory;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Remembers which vendor ticket ids already have a check-in mirror enqueued — by the
/// live gate path (<c>GateController.Decision</c>) or by the backfill page — so the
/// backfill's single-row test and bulk send can never re-post one while the vendor's
/// check-in hasn't flowed back through the ticket sync yet (TicketTailor check-ins
/// double-record on repeat). Single-server in-memory state, like
/// <see cref="GatePinThrottle"/>; entries expire after <see cref="Retention"/> — by
/// then the sync has long since moved the row out of pending (or the app restarted,
/// in which case the page's "wait for the sync" copy is the guard).
/// </summary>
public sealed class GateVendorMirrorLedger(IMemoryCache cache)
{
    /// <summary>How long a sent id stays remembered — generously past any sync cadence.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private static string Key(string vendorTicketId) => $"GateVendorMirrorSent:{vendorTicketId}";

    public void MarkSent(string vendorTicketId) =>
        cache.Set(Key(vendorTicketId), true, Retention);

    public bool WasSent(string vendorTicketId) =>
        cache.TryGetValue(Key(vendorTicketId), out _);
}
