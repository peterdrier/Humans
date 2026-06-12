using System.Collections.Concurrent;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <inheritdoc cref="IClientStatsTracker"/>
public sealed class ClientStatsTracker : IClientStatsTracker
{
    // OS / browser / device / bot labels come from UserAgentClassifier's bounded
    // vocabulary, so these dictionaries cannot grow without limit. _bots breaks the
    // collapsed "Bot" bucket down by the specific crawler name.
    private readonly ConcurrentDictionary<string, long> _operatingSystems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _browsers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _deviceTypes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _bots = new(StringComparer.Ordinal);

    // Resolution is fed by an anonymous beacon, so distinct buckets are soft-capped
    // to bound memory; once the cap is reached new keys fold into an "Other" bucket.
    private const int MaxResolutionBuckets = 200;
    private readonly ConcurrentDictionary<string, long> _resolutions = new(StringComparer.Ordinal);

    // Rolling buffer of error responses, same enqueue-then-trim-under-lock pattern
    // as InMemoryLogSink (ConcurrentQueue.Count is a snapshot, so unlocked writers
    // could both see "over capacity" and over-evict; readers stay lock-free).
    // URL and UA are truncated on storage: 1000 entries × ~600 B stays under 1 MB.
    private const int ErrorCapacity = 1000;
    private const int MaxUrlLength = 200;
    private const int MaxUserAgentLength = 150;
    private readonly ConcurrentQueue<ClientErrorEntry> _errors = new();
    private readonly Lock _errorsLock = new();
    private readonly ConcurrentDictionary<int, long> _errorCounts = new();

    private long _totalPageViews;
    private long _totalResolutionSamples;

    public void RecordPageView(string? userAgent)
    {
        var c = UserAgentClassifier.Classify(userAgent);
        Interlocked.Increment(ref _totalPageViews);
        Bump(_operatingSystems, c.Os);
        Bump(_browsers, c.Browser);
        Bump(_deviceTypes, c.Device);
        if (c.BotName is { } bot)
            Bump(_bots, bot);
    }

    public void RecordResolution(int screenWidth, int screenHeight)
    {
        // Reject implausible values from the untrusted beacon.
        if (screenWidth is < 1 or > 16384 || screenHeight is < 1 or > 16384)
            return;

        Interlocked.Increment(ref _totalResolutionSamples);

        var key = $"{screenWidth}x{screenHeight}";
        if (_resolutions.ContainsKey(key))
            Bump(_resolutions, key);
        else if (_resolutions.Count < MaxResolutionBuckets)
            // Soft cap: a check-then-add race can add a handful of buckets past the
            // cap under concurrent first-sightings, after which the gate stays closed
            // (keys are never removed). Bounded by concurrency, not unbounded.
            Bump(_resolutions, key);
        else
            Bump(_resolutions, "Other");
    }

    public void RecordError(ClientErrorEntry entry)
    {
        _errorCounts.AddOrUpdate(entry.StatusCode, 1, static (_, v) => v + 1);

        // Classify before truncation so the label sees the full UA string.
        var c = UserAgentClassifier.Classify(entry.UserAgent);
        var label = c.BotName
            ?? (c is { Browser: "Unknown", Os: "Unknown" } ? "Unknown" : $"{c.Browser} · {c.Os}");

        entry = entry with
        {
            Url = Truncate(entry.Url, MaxUrlLength),
            UserAgent = Truncate(entry.UserAgent, MaxUserAgentLength),
            ClientLabel = label
        };

        lock (_errorsLock)
        {
            _errors.Enqueue(entry);
            while (_errors.Count > ErrorCapacity)
                _errors.TryDequeue(out _);
        }
    }

    public ClientErrorsSnapshot GetErrorsSnapshot(int count) => new(
        TotalErrors: _errorCounts.Values.Sum(),
        LifetimeCounts: _errorCounts.ToDictionary(kv => kv.Key, kv => kv.Value),
        // Queue enumeration is oldest-first; reverse for newest-first display.
        Recent: _errors.Reverse().Take(count).ToList());

    public ClientStatsSnapshot GetSnapshot() => new(
        TotalPageViews: Interlocked.Read(ref _totalPageViews),
        OperatingSystems: Rank(_operatingSystems),
        Browsers: Rank(_browsers),
        DeviceTypes: Rank(_deviceTypes),
        Bots: Rank(_bots),
        TotalResolutionSamples: Interlocked.Read(ref _totalResolutionSamples),
        Resolutions: Rank(_resolutions));

    private static void Bump(ConcurrentDictionary<string, long> map, string key)
        => map.AddOrUpdate(key, 1, static (_, v) => v + 1);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static IReadOnlyList<ClientStatCount> Rank(ConcurrentDictionary<string, long> map)
        => map.Select(kv => new ClientStatCount(kv.Key, kv.Value))
              .OrderByDescending(c => c.Count)
              .ThenBy(c => c.Label, StringComparer.Ordinal)
              .ToList();
}
