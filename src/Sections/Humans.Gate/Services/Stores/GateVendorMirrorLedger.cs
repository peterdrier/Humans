using Microsoft.Extensions.Caching.Memory;

namespace Humans.Gate.Services.Stores;

/// <summary>
/// Vendor ticket ids with a check-in mirror already enqueued, live or by backfill, so the backfill never re-posts one
/// before the ticket sync confirms it (check-ins double-record). In memory: after a restart the page's "wait for the
/// sync" copy is the guard.
/// </summary>
internal sealed class GateVendorMirrorLedger(IMemoryCache cache)
{
    /// <summary>Generously past any sync cadence.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly Lock _lock = new();

    private static string Key(string vendorTicketId) => $"GateVendorMirrorSent:{vendorTicketId}";

    /// <summary>
    /// True when this call claimed the id, so the caller may enqueue. Check-then-act under one lock: overlapping sends
    /// must not both enqueue the same non-idempotent check-in.
    /// </summary>
    public bool TryMarkSent(string vendorTicketId)
    {
        lock (_lock)
        {
            var key = Key(vendorTicketId);
            if (cache.TryGetValue(key, out _))
                return false;
            cache.Set(key, true, Retention);
            return true;
        }
    }

    public bool WasSent(string vendorTicketId) =>
        cache.TryGetValue(Key(vendorTicketId), out _);
}
