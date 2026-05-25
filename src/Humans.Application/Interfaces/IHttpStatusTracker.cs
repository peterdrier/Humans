namespace Humans.Application.Interfaces;

/// <summary>
/// Process-local tally of HTTP response status codes, fed by a
/// <see cref="System.Diagnostics.Metrics.MeterListener"/> that passively observes
/// ASP.NET Core's built-in <c>http.server.request.duration</c> instrument. Powers
/// the status-code panel on the <c>/Admin/ClientStats</c> debug screen.
/// </summary>
/// <remarks>
/// In-memory only — counts reset on process restart. The listener observes the
/// same measurements the OpenTelemetry/Prometheus exporter already consumes; it
/// does not reset or interfere with that pipeline. Counts include every response
/// the host serves (static assets, probes, etc.), so the 2xx bucket is dominated
/// by non-page traffic — read it as a rough health signal, not page analytics.
/// </remarks>
public interface IHttpStatusTracker
{
    /// <summary>Total responses observed since process start.</summary>
    long Total { get; }

    /// <summary>Count of responses per HTTP status code since process start.</summary>
    IReadOnlyDictionary<int, long> GetCounts();
}
