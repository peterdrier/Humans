using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Serilog sink that keeps the last N log events in a circular buffer.
/// Used by the Admin/Logs page to display recent warnings and errors
/// without needing to query Docker logs.
/// </summary>
public sealed class InMemoryLogSink(int capacity = 1000) : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    private readonly ConcurrentDictionary<LogEventLevel, long> _lifetimeCounts = new();

    /// <summary>UTC timestamp captured when the sink (and process) started.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Total events emitted since startup, summed across all log levels.</summary>
    public long TotalEvents => _lifetimeCounts.Values.Sum();

    public void Emit(LogEvent logEvent)
    {
        _lifetimeCounts.AddOrUpdate(logEvent.Level, 1, (_, count) => count + 1);
        _events.Enqueue(logEvent);
        while (_events.Count > capacity)
            _events.TryDequeue(out _);
    }

    public IReadOnlyList<LogEvent> GetEvents(int count = 1000) =>
        _events.Reverse().Take(count).ToList();

    /// <summary>
    /// Returns cumulative counts per log level since application start.
    /// These survive ring buffer eviction.
    /// </summary>
    public IReadOnlyDictionary<LogEventLevel, long> GetLifetimeCounts() =>
        _lifetimeCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <summary>Singleton instance registered in DI and Serilog config.</summary>
    public static InMemoryLogSink Instance { get; } = new();
}
