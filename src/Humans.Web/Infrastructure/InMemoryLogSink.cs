using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Serilog sink that keeps the last N log events in a circular buffer.
/// Used by the Admin/Logs page to display recent warnings and errors
/// without needing to query Docker logs.
/// </summary>
public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    private readonly int _capacity;

    public InMemoryLogSink(int capacity = 200)
    {
        _capacity = capacity;
    }

    public void Emit(LogEvent logEvent)
    {
        _events.Enqueue(logEvent);
        while (_events.Count > _capacity)
            _events.TryDequeue(out _);
    }

    public IReadOnlyList<LogEvent> GetEvents(int count = 50) =>
        _events.Reverse().Take(count).ToList();

    /// <summary>Singleton instance registered in DI and Serilog config.</summary>
    public static InMemoryLogSink Instance { get; } = new();
}
