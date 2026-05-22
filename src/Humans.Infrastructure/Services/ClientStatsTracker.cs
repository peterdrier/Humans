using System.Collections.Concurrent;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <inheritdoc cref="IClientStatsTracker"/>
public sealed class ClientStatsTracker : IClientStatsTracker
{
    // OS / browser / device labels come from UserAgentClassifier's bounded
    // vocabulary, so these dictionaries cannot grow without limit.
    private readonly ConcurrentDictionary<string, long> _operatingSystems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _browsers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _deviceTypes = new(StringComparer.Ordinal);

    // Resolution is fed by an anonymous beacon, so distinct buckets are capped to
    // bound memory; overflow folds into an "Other" bucket.
    private const int MaxResolutionBuckets = 200;
    private readonly ConcurrentDictionary<string, long> _resolutions = new(StringComparer.Ordinal);

    private long _totalPageViews;
    private long _totalResolutionSamples;

    public void RecordPageView(string? userAgent)
    {
        var c = UserAgentClassifier.Classify(userAgent);
        Interlocked.Increment(ref _totalPageViews);
        Bump(_operatingSystems, c.Os);
        Bump(_browsers, c.Browser);
        Bump(_deviceTypes, c.Device);
    }

    public void RecordResolution(int screenWidth, int screenHeight)
    {
        // Reject implausible values from the untrusted beacon.
        if (screenWidth is < 1 or > 16384 || screenHeight is < 1 or > 16384)
            return;

        Interlocked.Increment(ref _totalResolutionSamples);

        var key = $"{screenWidth}x{screenHeight}";
        if (_resolutions.ContainsKey(key) || _resolutions.Count < MaxResolutionBuckets)
            Bump(_resolutions, key);
        else
            Bump(_resolutions, "Other");
    }

    public ClientStatsSnapshot GetSnapshot() => new(
        TotalPageViews: Interlocked.Read(ref _totalPageViews),
        OperatingSystems: Rank(_operatingSystems),
        Browsers: Rank(_browsers),
        DeviceTypes: Rank(_deviceTypes),
        TotalResolutionSamples: Interlocked.Read(ref _totalResolutionSamples),
        Resolutions: Rank(_resolutions));

    private static void Bump(ConcurrentDictionary<string, long> map, string key)
        => map.AddOrUpdate(key, 1, static (_, v) => v + 1);

    private static IReadOnlyList<ClientStatCount> Rank(ConcurrentDictionary<string, long> map)
        => map.Select(kv => new ClientStatCount(kv.Key, kv.Value))
              .OrderByDescending(c => c.Count)
              .ThenBy(c => c.Label, StringComparer.Ordinal)
              .ToList();
}
