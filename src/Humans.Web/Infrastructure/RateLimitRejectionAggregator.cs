namespace Humans.Web.Infrastructure;

/// <summary>
/// Collapses rate-limit rejection warnings into one detailed line plus a
/// per-source-IP summary. A single bot sweep otherwise produces dozens of
/// near-identical warnings — each paying a reverse-DNS lookup — drowning real
/// signal. The first rejection from an IP in a window returns true so the
/// caller logs full detail; later rejections only bump a counter. A one-shot
/// timer flushes the summary when the window closes — on time even if the
/// burst has stopped — and skips it when only one rejection occurred (that one
/// was already logged in detail).
/// Deliberately not the <c>GateLoginThrottle</c>-style IMemoryCache+IClock
/// idiom: cache eviction callbacks fire lazily on later cache activity, which
/// can't deliver the "summary lands within ~60s even if the burst stops"
/// requirement — that needs a timer.
/// </summary>
public sealed class RateLimitRejectionAggregator(ILogger logger, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(60);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = [];

    private sealed class Entry
    {
        public int Count;

        // Held so the one-shot flush timer isn't garbage-collected before it fires.
        public Timer? Timer;
    }

    /// <summary>
    /// Counts one rejection from <paramref name="sourceIp"/>. True when it is
    /// the first in the current window — the caller should log full detail.
    /// </summary>
    public bool RecordRejection(string sourceIp)
    {
        Entry entry;
        lock (_lock)
        {
            if (_entries.TryGetValue(sourceIp, out var existing))
            {
                existing.Count++;
                return false;
            }

            entry = new Entry { Count = 1 };
            _entries[sourceIp] = entry;
        }

        entry.Timer = new Timer(_ => Flush(sourceIp), null, _window, Timeout.InfiniteTimeSpan);
        return true;
    }

    private void Flush(string sourceIp)
    {
        Entry? entry;
        lock (_lock)
        {
            if (!_entries.Remove(sourceIp, out entry))
                return;
        }

        entry.Timer?.Dispose();
        if (entry.Count > 1)
        {
            logger.LogWarning(
                "Rate limit exceeded by {SourceIp}, {Count} times over the last {WindowSeconds} seconds",
                sourceIp, entry.Count, (int)_window.TotalSeconds);
        }
    }
}
