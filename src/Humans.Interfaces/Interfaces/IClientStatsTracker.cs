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

    /// <summary>
    /// Record one error response (status &gt; 399, or an aborted request recorded
    /// as 499) into the rolling buffer. URL and User-Agent are truncated on
    /// storage to bound memory.
    /// </summary>
    void RecordError(ClientErrorEntry entry);

    /// <summary>
    /// The most recent error responses, newest first, up to <paramref name="count"/>,
    /// plus lifetime per-status-code counts that survive buffer eviction.
    /// </summary>
    ClientErrorsSnapshot GetErrorsSnapshot(int count);
}

/// <summary>Immutable view of the client-stats counters at a point in time.</summary>
public sealed record ClientStatsSnapshot(
    long TotalPageViews,
    IReadOnlyList<ClientStatCount> OperatingSystems,
    IReadOnlyList<ClientStatCount> Browsers,
    IReadOnlyList<ClientStatCount> DeviceTypes,
    IReadOnlyList<ClientStatCount> Bots,
    long TotalResolutionSamples,
    IReadOnlyList<ClientStatCount> Resolutions);

/// <summary>A single labelled count (e.g. <c>"Windows" → 42</c>).</summary>
public sealed record ClientStatCount(string Label, long Count);

/// <summary>
/// One error response captured for the <c>/Debug/HttpErrors</c> rolling buffer.
/// <paramref name="UserId"/> is the authenticated user's id when the request
/// carried one — bot noise and anonymous traffic leave it null.
/// <paramref name="ClientLabel"/> is derived from <paramref name="UserAgent"/>
/// by the tracker at record time (bot name, or browser · OS); callers leave it
/// at its default.
/// </summary>
public sealed record ClientErrorEntry(
    NodaTime.Instant Timestamp,
    int StatusCode,
    string Method,
    string Url,
    string IpAddress,
    Guid? UserId,
    string UserAgent,
    string ClientLabel = "");

/// <summary>Rolling-buffer view: recent entries (newest first) plus lifetime per-code counts.</summary>
public sealed record ClientErrorsSnapshot(
    long TotalErrors,
    IReadOnlyDictionary<int, long> LifetimeCounts,
    IReadOnlyList<ClientErrorEntry> Recent);
