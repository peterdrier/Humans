using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Gate.Services.Stores;

/// <summary>
/// A 4-digit PIN is a small secret, so a locked key stays locked until a correct PIN (<see cref="Reset"/>). Claims
/// throttle per target user, never per IP: the proxy collapses kiosk traffic to one IP, so an IP key would freeze
/// every claim. The shared override PIN uses one terminal-wide bucket that blocks only overrides.
/// </summary>
internal sealed class GatePinThrottle(IMemoryCache cache, IClock clock)
{
    public const int MaxFailures = 5;

    public static readonly Duration Lockout = Duration.FromMinutes(15);

    private readonly Lock _lock = new();

    private sealed class State
    {
        public int Count;
        public Instant? LockedUntil;
    }

    private static string Key(string key) => $"GatePinFailures:{key}";

    /// <summary>Null when entry is allowed; otherwise whole seconds left on the lockout (≥ 1).</summary>
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

            var ttl = state.LockedUntil is { } until
                ? until - clock.GetCurrentInstant()
                : Lockout;
            cache.Set(cacheKey, state, ttl.ToTimeSpan());
        }
    }

    public void Reset(string key)
    {
        lock (_lock)
        {
            cache.Remove(Key(key));
        }
    }
}
