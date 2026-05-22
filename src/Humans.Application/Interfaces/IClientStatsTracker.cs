namespace Humans.Application.Interfaces;

/// <summary>
/// Process-local aggregator for coarse client demographics — operating system,
/// browser family and device class (derived from the User-Agent header) plus
/// screen resolution (reported by a client-side beacon). Powers the
/// <c>/Admin/ClientStats</c> debug screen.
/// </summary>
/// <remarks>
/// In-memory only — counts reset on process restart (i.e. every redeploy). OS,
/// browser and device labels come from a bounded vocabulary so their cardinality
/// is naturally limited; resolution buckets are fed by an anonymous beacon and so
/// are capped explicitly. This is a rough debug aid, not analytics — see
/// <see cref="IHttpStatusTracker"/> for the companion status-code tally.
/// </remarks>
public interface IClientStatsTracker
{
    /// <summary>Classify one page view from its <paramref name="userAgent"/> and tally it.</summary>
    void RecordPageView(string? userAgent);

    /// <summary>Tally one screen-resolution sample reported by the browser beacon.</summary>
    void RecordResolution(int screenWidth, int screenHeight);

    /// <summary>Snapshot of all counts since process start.</summary>
    ClientStatsSnapshot GetSnapshot();
}

/// <summary>Immutable view of the client-stats counters at a point in time.</summary>
public sealed record ClientStatsSnapshot(
    long TotalPageViews,
    IReadOnlyList<ClientStatCount> OperatingSystems,
    IReadOnlyList<ClientStatCount> Browsers,
    IReadOnlyList<ClientStatCount> DeviceTypes,
    long TotalResolutionSamples,
    IReadOnlyList<ClientStatCount> Resolutions);

/// <summary>A single labelled count (e.g. <c>"Windows" → 42</c>).</summary>
public sealed record ClientStatCount(string Label, long Count);
