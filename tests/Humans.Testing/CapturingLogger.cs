using Microsoft.Extensions.Logging;

namespace Humans.Testing;

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that captures log entries in memory so
/// tests can assert on level and message without involving a real logging
/// framework.
/// </summary>
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
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
