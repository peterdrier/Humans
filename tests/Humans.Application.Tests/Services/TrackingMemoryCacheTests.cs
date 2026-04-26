using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TrackingMemoryCacheTests : IDisposable
{
    private readonly MemoryCache _inner;
    private readonly TrackingMemoryCache _tracker;

    public TrackingMemoryCacheTests()
    {
        _inner = new MemoryCache(new MemoryCacheOptions());
        _tracker = new TrackingMemoryCache(_inner);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public void TryGetValue_RecordsHitWhenKeyExists()
    {
        _inner.Set("Profile:abc", "cached-value");

        _tracker.TryGetValue("Profile:abc", out _);

        var stats = _tracker.GetSnapshot();
        stats.Should().ContainSingle();
        stats[0].KeyType.Should().Be("Profile");
        stats[0].Hits.Should().Be(1);
        stats[0].Misses.Should().Be(0);
    }

    [HumansFact]
    public void TryGetValue_RecordsMissWhenKeyAbsent()
    {
        _tracker.TryGetValue("Profile:xyz", out _);

        var stats = _tracker.GetSnapshot();
        stats.Should().ContainSingle();
        stats[0].KeyType.Should().Be("Profile");
        stats[0].Hits.Should().Be(0);
        stats[0].Misses.Should().Be(1);
    }

    [HumansFact]
    public void DeriveKeyType_ExtractsPrefixBeforeColon()
    {
        _tracker.TryGetValue("UserTicketCount:12345", out _);
        _tracker.TryGetValue("UserProfile:67890", out _);

        var stats = _tracker.GetSnapshot();
        stats.Should().HaveCount(2);
        stats.Should().Contain(e => e.KeyType == "UserTicketCount");
        stats.Should().Contain(e => e.KeyType == "UserProfile");
    }

    [HumansFact]
    public void DeriveKeyType_UseFullKeyWhenNoColon()
    {
        _tracker.TryGetValue("NavBadgeCounts", out _);

        var stats = _tracker.GetSnapshot();
        stats.Should().ContainSingle();
        stats[0].KeyType.Should().Be("NavBadgeCounts");
    }

    [HumansFact]
    public void Reset_ClearsAllStats()
    {
        _inner.Set("Profile:a", "value");
        _tracker.TryGetValue("Profile:a", out _);
        _tracker.TryGetValue("Profile:b", out _);

        _tracker.Reset();

        _tracker.GetSnapshot().Should().BeEmpty();
        _tracker.TotalHits.Should().Be(0);
        _tracker.TotalMisses.Should().Be(0);
    }

    [HumansFact]
    public void TotalHitsAndMisses_AggregateAcrossKeyTypes()
    {
        _inner.Set("Profile:a", "value");
        _tracker.TryGetValue("Profile:a", out _); // hit
        _tracker.TryGetValue("Profile:b", out _); // miss
        _tracker.TryGetValue("Teams:x", out _);   // miss

        _tracker.TotalHits.Should().Be(1);
        _tracker.TotalMisses.Should().Be(2);
    }

    [HumansFact]
    public void HitRatePercent_CalculatesCorrectly()
    {
        _inner.Set("Profile:a", "value");
        _tracker.TryGetValue("Profile:a", out _); // hit
        _tracker.TryGetValue("Profile:b", out _); // miss

        var stats = _tracker.GetSnapshot();
        stats[0].HitRatePercent.Should().Be(50.0);
    }

    [HumansFact]
    public void GetSnapshot_OrderedByTotalAccessCountDescending()
    {
        // Profile: 3 accesses
        _tracker.TryGetValue("Profile:a", out _);
        _tracker.TryGetValue("Profile:b", out _);
        _tracker.TryGetValue("Profile:c", out _);

        // Teams: 1 access
        _tracker.TryGetValue("Teams:x", out _);

        var stats = _tracker.GetSnapshot();
        stats[0].KeyType.Should().Be("Profile");
        stats[1].KeyType.Should().Be("Teams");
    }
}
