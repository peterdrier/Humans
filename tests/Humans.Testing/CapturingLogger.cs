using Microsoft.Extensions.Logging;

namespace Humans.Testing;

public sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var entry = new LogEntry(logLevel, formatter(state, exception), exception);
        // Services under test may log from concurrent tasks (Task.WhenAll).
        lock (Entries)
        {
            Entries.Add(entry);
        }
    }
}
