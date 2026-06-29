using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Brute-force throttle for personal gate PIN entry (claim take-over and supervisor
/// override). Stricter than <see cref="GateLoginThrottle"/>: a 4-digit PIN is a small
/// secret, so after a few failures the key is locked for a real cooldown that does NOT
/// reset on its own — only a correct PIN (<see cref="Reset"/>) clears it.
///
/// Callers throttle on BOTH the target user-id AND the source device/IP, so neither a
/// single supervisor's PIN nor one kiosk can be ground through the ~10k space: check
/// both keys before verifying, record a failure on both, reset both on success.
/// </summary>
public sealed class GatePinThrottle(IMemoryCache cache, IClock clock)
{
    /// <summary>Failed attempts allowed before a key is locked out.</summary>
    public const int MaxFailures = 5;

    /// <summary>How long a key stays locked after <see cref="MaxFailures"/> failures.</summary>
    public static readonly Duration Lockout = Duration.FromMinutes(15);

    private readonly Lock _lock = new();

    private sealed class State
    {
        public int Count;
        public Instant? LockedUntil;
    }

    private static string Key(string key) => $"GatePinFailures:{key}";

    /// <summary>Null when entry for this key is allowed; otherwise whole seconds left on the lockout (≥ 1).</summary>
    public int? SecondsUntilRetry(string key)
    {
        lock (_lock)
        {
            if (!cache.TryGetValue(Key(key), out State? state) || state?.LockedUntil is not { } until)
                return null;

            var remaining = until - clock.GetCurrentInstant();
            return remaining <= Duration.Zero ? null : (int)Math.Ceiling(remaining.TotalSeconds);
        }
    }

    public void RecordFailure(string key)
    {
        lock (_lock)
        {
            var cacheKey = Key(key);
            var state = cache.TryGetValue(cacheKey, out State? existing) && existing is not null ? existing : new State();
            state.Count++;
            if (state.Count >= MaxFailures)
                state.LockedUntil = clock.GetCurrentInstant() + Lockout;

            // Keep the record alive at least until the lockout clears (or the decay window).
            var ttl = state.LockedUntil is { } until
                ? until - clock.GetCurrentInstant()
                : Lockout;
            cache.Set(cacheKey, state, ttl.ToTimeSpan());
        }
    }

    /// <summary>A correct PIN clears the key's failure history.</summary>
    public void Reset(string key)
    {
        lock (_lock)
        {
            cache.Remove(Key(key));
        }
    }
}
