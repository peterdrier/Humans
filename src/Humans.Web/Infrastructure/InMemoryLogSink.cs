using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Serilog sink that keeps the last N log events in per-severity circular buffers.
/// Warnings go to their own buffer; Error and Fatal share a separate buffer so that
/// high-volume warnings cannot evict error events.
/// Used by the Admin/Logs page to display recent warnings and errors
/// without needing to query Docker logs.
/// </summary>
public sealed class InMemoryLogSink(int warningCapacity = 1000, int errorCapacity = 1000) : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _warnings = new();
    private readonly ConcurrentQueue<LogEvent> _errors = new();
    private readonly ConcurrentDictionary<LogEventLevel, long> _lifetimeCounts = new();

    // Writer-side locks: ConcurrentQueue.Count is a snapshot, so unlocked enqueue-then-trim
    // lets two concurrent writers both see "over capacity" and over-evict. Readers enumerate
    // the queues lock-free; that stays safe.
    private readonly Lock _warningsLock = new();
    private readonly Lock _errorsLock = new();

    /// <summary>UTC timestamp captured when the sink (and process) started.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Total events emitted since startup, summed across all log levels.</summary>
    public long TotalEvents => _lifetimeCounts.Values.Sum();

    public void Emit(LogEvent logEvent)
    {
        // The pipeline filters at Warning today; this floor keeps a future lowered minimum
        // (e.g. Information during a diagnostic session) from flooding the warning buffer
        // and evicting the events this sink exists to retain.
        if (logEvent.Level < LogEventLevel.Warning)
            return;

        _lifetimeCounts.AddOrUpdate(logEvent.Level, 1, (_, count) => count + 1);

        if (logEvent.Level >= LogEventLevel.Error)
        {
            lock (_errorsLock)
            {
                _errors.Enqueue(logEvent);
                while (_errors.Count > errorCapacity)
                    _errors.TryDequeue(out _);
            }
        }
        else
        {
            lock (_warningsLock)
            {
                _warnings.Enqueue(logEvent);
                while (_warnings.Count > warningCapacity)
                    _warnings.TryDequeue(out _);
            }
        }
    }

    /// <summary>
    /// Returns buffered events, newest first, up to <paramref name="count"/> entries.
    /// When <paramref name="minLevel"/> is supplied only events at or above that level
    /// are included; omit it (or pass <c>null</c>) for all buffered levels.
    /// </summary>
    public IReadOnlyList<LogEvent> GetEvents(int count = 1000, LogEventLevel? minLevel = null)
    {
        var source = minLevel >= LogEventLevel.Error
            ? _errors.AsEnumerable()
            : _warnings.Concat(_errors);

        return source
            .Where(e => minLevel == null || e.Level >= minLevel)
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Returns cumulative counts per log level since application start.
    /// These survive ring buffer eviction.
    /// </summary>
    public IReadOnlyDictionary<LogEventLevel, long> GetLifetimeCounts() =>
        _lifetimeCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <summary>Singleton instance registered in DI and Serilog config.</summary>
    public static InMemoryLogSink Instance { get; } = new();
}
