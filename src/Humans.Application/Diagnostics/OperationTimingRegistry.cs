using NodaTime;

namespace Humans.Application.Diagnostics;

/// <summary>
/// In-process registry of operation timing aggregates and swallowed-exception counts.
/// Thread-safe via per-entry locks; designed for a single-server deployment at ~500 users.
/// </summary>
public sealed class OperationTimingRegistry
{
    public static readonly OperationTimingRegistry Instance = new();

    private readonly Dictionary<string, OperationAggregate> _timings =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, long> _swallowed =
        new(StringComparer.Ordinal);

    private readonly Lock _timingsLock = new();
    private readonly Lock _swallowedLock = new();

    public void Record(string key, double elapsedMs)
    {
        lock (_timingsLock)
        {
            if (!_timings.TryGetValue(key, out var agg))
            {
                agg = new OperationAggregate();
                _timings[key] = agg;
            }
            agg.Add(elapsedMs);
        }
    }

    public void IncrementSwallowed(string key)
    {
        lock (_swallowedLock)
        {
            _swallowed.TryGetValue(key, out var count);
            _swallowed[key] = count + 1;
        }
    }

    public IReadOnlyList<OperationTimingSnapshot> GetTimings()
    {
        lock (_timingsLock)
        {
            return _timings
                .Select(kvp => kvp.Value.ToSnapshot(kvp.Key))
                .ToList();
        }
    }

    public IReadOnlyList<SwallowedExceptionSnapshot> GetSwallowed()
    {
        lock (_swallowedLock)
        {
            return _swallowed
                .Select(kvp => new SwallowedExceptionSnapshot(kvp.Key, kvp.Value))
                .ToList();
        }
    }

    // Mutable aggregate kept in the dictionary; access is always under _timingsLock.
    private sealed class OperationAggregate
    {
        private long _count;
        private double _totalMs;
        private double _minMs = double.MaxValue;
        private double _maxMs;
        private double _lastMs;
        private Instant _lastAt;

        public void Add(double ms)
        {
            _count++;
            _totalMs += ms;
            if (ms < _minMs) _minMs = ms;
            if (ms > _maxMs) _maxMs = ms;
            _lastMs = ms;
            _lastAt = SystemClock.Instance.GetCurrentInstant();
        }

        public OperationTimingSnapshot ToSnapshot(string key) => new(
            Key: key,
            Count: _count,
            TotalMs: _totalMs,
            MinMs: _count > 0 ? _minMs : 0,
            MaxMs: _maxMs,
            LastMs: _lastMs,
            LastAt: _lastAt);
    }
}

public sealed record OperationTimingSnapshot(
    string Key,
    long Count,
    double TotalMs,
    double MinMs,
    double MaxMs,
    double LastMs,
    Instant LastAt)
{
    public double AvgMs => Count > 0 ? TotalMs / Count : 0;
}

public sealed record SwallowedExceptionSnapshot(string Key, long Count);
