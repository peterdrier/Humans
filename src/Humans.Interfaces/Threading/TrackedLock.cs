using Microsoft.Extensions.Logging;

namespace Humans.Application.Threading;

/// <summary>
/// A named mutex wrapper around <see cref="SemaphoreSlim"/> that measures wait time,
/// logs slow acquisitions with escalating severity, and converts hard-lock timeouts
/// into diagnosable <see cref="TimeoutException"/> failures.
/// </summary>
/// <remarks>
/// The logger is passed at acquire time (not construction) so instances can be stored
/// in static fields without a DI dependency.
/// </remarks>
public sealed class TrackedLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly TimeSpan _debugThreshold;
    private readonly TimeSpan _informationThreshold;
    private readonly TimeSpan _warningThreshold;
    private readonly TimeSpan _timeout;

    public string Name { get; }

    /// <summary>Creates a named lock with optional timeout and log-threshold overrides.</summary>
    /// <param name="name">Human-readable name used in log messages and exception text.</param>
    /// <param name="timeout">Maximum time to wait before throwing <see cref="TimeoutException"/>. Defaults to 60 seconds.</param>
    /// <param name="debugThreshold">Wait ≥ this logs at Debug. Defaults to 1 second.</param>
    /// <param name="informationThreshold">Wait ≥ this logs at Information. Defaults to 5 seconds.</param>
    /// <param name="warningThreshold">Wait ≥ this logs at Warning. Defaults to 15 seconds.</param>
    public TrackedLock(
        string name,
        TimeSpan? timeout = null,
        TimeSpan? debugThreshold = null,
        TimeSpan? informationThreshold = null,
        TimeSpan? warningThreshold = null)
    {
        Name = name;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _debugThreshold = debugThreshold ?? TimeSpan.FromSeconds(1);
        _informationThreshold = informationThreshold ?? TimeSpan.FromSeconds(5);
        _warningThreshold = warningThreshold ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Acquires the lock asynchronously. Returns a <see cref="Releaser"/> that releases
    /// it exactly once when disposed. Use in a <c>using var</c> statement.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// Thrown when the lock is not acquired within the configured timeout.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled while waiting.
    /// </exception>
    public async ValueTask<Releaser> AcquireAsync(ILogger logger, CancellationToken ct = default)
    {
        var start = TimeProvider.System.GetTimestamp();

        var acquired = await _semaphore.WaitAsync(_timeout, ct).ConfigureAwait(false);

        var elapsed = TimeProvider.System.GetElapsedTime(start);

        if (!acquired)
        {
            logger.LogError(
                "Lock '{LockName}' timed out after {ElapsedMs}ms waiting for acquisition",
                Name, (long)elapsed.TotalMilliseconds);
            throw new TimeoutException(
                $"Lock '{Name}' was not acquired within {_timeout.TotalSeconds:0}s.");
        }

        var level = WaitToLogLevel(elapsed, _debugThreshold, _informationThreshold, _warningThreshold);
        if (level.HasValue)
        {
            logger.Log(level.Value,
                "Lock '{LockName}' waited {ElapsedMs}ms before acquisition",
                Name, (long)elapsed.TotalMilliseconds);
        }

        return new Releaser(_semaphore);
    }

    /// <summary>
    /// Maps a wait duration to a log level using the configured thresholds.
    /// Returns null when the wait is below all thresholds (no log needed).
    /// Internal for direct unit testing.
    /// </summary>
    internal static LogLevel? WaitToLogLevel(
        TimeSpan elapsed,
        TimeSpan debugThreshold,
        TimeSpan informationThreshold,
        TimeSpan warningThreshold)
    {
        if (elapsed >= warningThreshold) return LogLevel.Warning;
        if (elapsed >= informationThreshold) return LogLevel.Information;
        if (elapsed >= debugThreshold) return LogLevel.Debug;
        return null;
    }

    /// <summary>
    /// Releases the underlying <see cref="SemaphoreSlim"/> exactly once on dispose.
    /// Double-dispose is safe and silently ignored, including across struct copies.
    /// </summary>
    public readonly struct Releaser : IDisposable
    {
        // A thin class wrapper holds the mutable release-once counter so the
        // outer struct stays readonly (no defensive-copy hazard) while still
        // supporting the single-release invariant across copies.
        private sealed class State(SemaphoreSlim semaphore)
        {
            private int _released;

            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    semaphore.Release();
            }
        }

        private readonly State? _state;

        internal Releaser(SemaphoreSlim semaphore) => _state = new State(semaphore);

        public void Dispose() => _state?.Release();
    }
}
