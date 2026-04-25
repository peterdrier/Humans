using System.Diagnostics.Metrics;
using Humans.Application.Metering;

namespace Humans.Application.Interfaces.Metering;

/// <summary>
/// Process-wide registry for OpenTelemetry-exported gauges and counters.
/// Leaf node in the DI graph: depends on <c>ILogger</c> only, never on any other
/// service. Each section registers its own metrics from its DI extension. All
/// registered instruments export through the existing
/// <c>AddMeter("Humans.Metrics")</c> subscription in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// All registration methods are idempotent by name. Re-registering with the
/// same metadata returns the existing instrument; re-registering with different
/// metadata logs a warning and keeps the original. Observable-gauge callbacks
/// are invoked at scrape time directly by OpenTelemetry — there is no separate
/// refresh tick. OTel catches per-instrument callback exceptions, so a failing
/// section gauge does not blank sibling gauges in the same scrape.
/// </remarks>
public interface IMeters
{
    /// <summary>
    /// Declares (or returns) a push-registered gauge. The caller holds the
    /// returned <see cref="IMeter"/> and calls <see cref="IMeter.Set"/> when
    /// the value changes.
    /// </summary>
    IMeter Declare(string name, MeterMetadata metadata);

    /// <summary>
    /// Registers a single-measurement observable gauge. <paramref name="observe"/>
    /// is invoked by OpenTelemetry at scrape time.
    /// </summary>
    void RegisterObservableGauge(string name, MeterMetadata metadata, Func<int> observe);

    /// <summary>
    /// Registers a multi-measurement observable gauge. Use when the gauge has
    /// dimensional tags (e.g. one measurement per status). <paramref name="observe"/>
    /// is invoked by OpenTelemetry at scrape time.
    /// </summary>
    void RegisterObservableGauge(
        string name,
        MeterMetadata metadata,
        Func<IEnumerable<Measurement<int>>> observe);

    /// <summary>
    /// Registers (or returns) a counter. Call sites add directly via
    /// <c>counter.Add(1, tag1, tag2)</c>.
    /// </summary>
    Counter<long> RegisterCounter(string name, MeterMetadata metadata);
}
