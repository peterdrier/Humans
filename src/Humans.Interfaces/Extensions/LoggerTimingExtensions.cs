using System.Diagnostics;
using System.Runtime.CompilerServices;
using Humans.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Extensions;

public static class LoggerTimingExtensions
{
    /// <summary>
    /// Starts a scoped timer. On dispose, records elapsed into <see cref="OperationTimingRegistry.Instance"/>
    /// and logs at a severity that escalates with duration (silent &lt;1 s, Debug ≥1 s, Info ≥5 s,
    /// Warning ≥15 s, Error ≥30 s).
    /// </summary>
    public static OperationTimer TimeOperation(
        this ILogger logger,
        string? detail = null,
        [CallerMemberName] string operation = "",
        [CallerFilePath] string filePath = "")
    {
        var className = DeriveClassName(filePath);
        var key = $"{className}.{operation}";
        return new OperationTimer(logger, key, detail);
    }

    /// <summary>
    /// Logs a deliberately-swallowed exception at Error and increments the swallowed-exception
    /// counter in <see cref="OperationTimingRegistry.Instance"/>.
    /// </summary>
    public static void Eat(
        this ILogger logger,
        Exception exception,
        string? detail = null,
        [CallerMemberName] string operation = "",
        [CallerFilePath] string filePath = "")
    {
        var className = DeriveClassName(filePath);
        var key = $"{className}.{operation}";
        OperationTimingRegistry.Instance.IncrementSwallowed(key);

        if (detail is not null)
            logger.LogError(exception, "Swallowed exception in {Operation} ({Detail})", key, detail);
        else
            logger.LogError(exception, "Swallowed exception in {Operation}", key);
    }

    // Visible to tests so the threshold logic can be verified without sleeping.
    internal static LogLevel SelectLogLevel(double elapsedMs) => elapsedMs switch
    {
        >= 30_000 => LogLevel.Error,
        >= 15_000 => LogLevel.Warning,
        >= 5_000 => LogLevel.Information,
        >= 1_000 => LogLevel.Debug,
        _ => LogLevel.None,
    };

    private static string DeriveClassName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return string.IsNullOrEmpty(name) ? "Unknown" : name;
    }
}

/// <summary>
/// Disposable timer returned by <see cref="LoggerTimingExtensions.TimeOperation"/>.
/// Seal as a class (not struct) so <c>using var</c> captures a reference that survives async continuations.
/// </summary>
public sealed class OperationTimer : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _key;
    private readonly string? _detail;
    private readonly Stopwatch _sw;
    private bool _disposed;

    internal OperationTimer(ILogger logger, string key, string? detail)
    {
        _logger = logger;
        _key = key;
        _detail = detail;
        _sw = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sw.Stop();
        var elapsedMs = _sw.Elapsed.TotalMilliseconds;

        OperationTimingRegistry.Instance.Record(_key, elapsedMs);

        var level = LoggerTimingExtensions.SelectLogLevel(elapsedMs);
        if (level == LogLevel.None) return;

        if (_detail is not null)
            _logger.Log(level, "{Operation} completed in {ElapsedMs:F1} ms ({Detail})", _key, elapsedMs, _detail);
        else
            _logger.Log(level, "{Operation} completed in {ElapsedMs:F1} ms", _key, elapsedMs);
    }
}
