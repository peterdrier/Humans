using System.Diagnostics.Metrics;

namespace Humans.Application.Interfaces;

/// <summary>
/// Registration surface for application-level OpenTelemetry counters and gauges.
/// Owns the singleton <c>Humans.Metrics</c> <see cref="Meter"/> and the 60-second
/// gauge-snapshot refresh tick; has zero section knowledge.
/// </summary>
/// <remarks>
/// <para>
/// Sections register their own counters and gauges through this interface via
/// <see cref="IMetricsContributor"/> — see issue nobodies-collective/Humans#580.
/// Counter instances are returned to the caller so the owning section can
/// increment them directly at the emission site; the pre-#580
/// <c>RecordEmailSent</c>/<c>RecordJobRun</c>/etc. methods on this interface
/// are gone and call sites now own their own <see cref="Counter{T}"/> references.
/// </para>
/// <para>
/// Gauge callbacks read from section-owned in-memory state; the section's
/// registered <see cref="RegisterGaugeRefresher"/> callback is invoked every
/// ~60 seconds on the snapshot tick to refresh that state from its owning data
/// source. A failing refresh callback is logged and isolated — one section's
/// exception cannot blank another section's gauges.
/// </para>
/// </remarks>
public interface IHumansMetrics
{
    /// <summary>
    /// Creates a counter on the <c>Humans.Metrics</c> meter and returns the
    /// instance. The caller owns the returned <see cref="Counter{T}"/> and calls
    /// <c>Add</c> directly; the metrics registry has no knowledge of counter
    /// names or tags.
    /// </summary>
    Counter<T> CreateCounter<T>(string name, string? description = null) where T : struct;

    /// <summary>
    /// Registers a single-value observable gauge. <paramref name="observeValue"/>
    /// is invoked by the OTel scrape pipeline and must read from section-owned
    /// in-memory state — do not perform I/O in the callback.
    /// </summary>
    void CreateObservableGauge<T>(string name, Func<T> observeValue, string? description = null) where T : struct;

    /// <summary>
    /// Registers a multi-measurement observable gauge (emits multiple tagged
    /// values per scrape — used for <c>status</c>-tagged or <c>role</c>-tagged
    /// dimensions). <paramref name="observeValues"/> must read from section-owned
    /// in-memory state; do not perform I/O in the callback.
    /// </summary>
    void CreateObservableGauge<T>(
        string name,
        Func<IEnumerable<Measurement<T>>> observeValues,
        string? description = null) where T : struct;

    /// <summary>
    /// Registers a refresh callback invoked every ~60 seconds on the snapshot
    /// tick. The callback is responsible for re-reading the section's data (via
    /// its own repository/service inside a scope obtained from
    /// <see cref="IServiceScopeFactory"/>) and updating the in-memory state that
    /// its gauge callbacks read from. Per-callback try/catch in the registry
    /// isolates failures.
    /// </summary>
    void RegisterGaugeRefresher(Func<CancellationToken, Task> refresher);
}

/// <summary>
/// Sections implement <see cref="IMetricsContributor"/> to participate in the
/// push-model metrics registration (issue nobodies-collective/Humans#580). The
/// registry eagerly resolves every registered contributor at startup and calls
/// <see cref="Initialize"/> exactly once, passing the shared
/// <see cref="IHumansMetrics"/> registration surface.
/// </summary>
public interface IMetricsContributor
{
    /// <summary>
    /// Called once at application startup. The contributor creates its counters
    /// and gauges on <paramref name="metrics"/>, stores the returned
    /// <see cref="Counter{T}"/> references on its own fields, and registers any
    /// gauge refresh callbacks via <see cref="IHumansMetrics.RegisterGaugeRefresher"/>.
    /// </summary>
    void Initialize(IHumansMetrics metrics);
}
