using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Per-source-IP failure throttle for the gate-terminal sign-in.
/// Identity's per-account lockout is the wrong axis for a shared kiosk account
/// with a public username: anyone failing passwords on purpose would lock the
/// account itself — denying the real terminal at gate. Throttling by source IP
/// means an attacker only ever locks themselves out. The trade-off (an attacker
/// on the same NAT as the gate laptop shares its bucket) is accepted and the
/// user-facing error spells it out.
/// </summary>
public sealed class GateLoginThrottle(IMemoryCache cache, IClock clock)
{
    public const int MaxFailuresPerWindow = 10;
    public static readonly Duration Window = Duration.FromMinutes(1);

    private readonly Lock _lock = new();

    private sealed class FailureWindow
    {
        public int Count;
        public Instant WindowStart;
    }

    private static string Key(string source) => $"GateLoginFailures:{source}";

    /// <summary>
    /// Null when a sign-in attempt from this source is allowed; otherwise the
    /// whole seconds remaining until the failure window expires (always ≥ 1).
    /// </summary>
    public int? SecondsUntilRetry(string source)
    {
        lock (_lock)
        {
            if (!cache.TryGetValue(Key(source), out FailureWindow? window) || window is null)
                return null;

            if (window.Count < MaxFailuresPerWindow)
                return null;

            var remaining = window.WindowStart + Window - clock.GetCurrentInstant();
            return remaining <= Duration.Zero
                ? null
                : (int)Math.Ceiling(remaining.TotalSeconds);
        }
    }

    public void RecordFailure(string source)
    {
        lock (_lock)
        {
            var key = Key(source);
            if (cache.TryGetValue(key, out FailureWindow? window) && window is not null)
            {
                window.Count++;
                return;
            }

            cache.Set(
                key,
                new FailureWindow { Count = 1, WindowStart = clock.GetCurrentInstant() },
                Window.ToTimeSpan());
        }
    }

    /// <summary>Successful sign-in clears the source's failure history.</summary>
    public void Reset(string source)
    {
        lock (_lock)
        {
            cache.Remove(Key(source));
        }
    }
}
