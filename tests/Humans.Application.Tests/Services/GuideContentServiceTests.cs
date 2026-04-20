using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class GuideContentServiceTests
{
    private sealed class FakeSource : IGuideContentSource
    {
        public int Calls { get; private set; }
        public Func<string, string> MarkdownFor { get; set; } = stem => $"# {stem}\n\nContent.";
        public Func<string, Exception?> FailFor { get; set; } = _ => null;

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default)
        {
            Calls++;
            var fail = FailFor(fileStem);
            if (fail is not null) throw fail;
            return Task.FromResult(MarkdownFor(fileStem));
        }
    }

    private sealed class StubRenderer : IGuideRenderer
    {
        public string Render(string markdown, string fileStem) => $"[rendered:{fileStem}]";
    }

    private static GuideContentService CreateService(FakeSource source, out IMemoryCache cache)
    {
        cache = new MemoryCache(new MemoryCacheOptions());
        var settings = Options.Create(new GuideSettings { CacheTtlHours = 6 });
        return new GuideContentService(
            source,
            new StubRenderer(),
            cache,
            settings,
            NullLogger<GuideContentService>.Instance);
    }

    [Fact]
    public async Task GetRenderedAsync_FirstCall_FetchesFromSource()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);

        var html = await service.GetRenderedAsync("Profiles", CancellationToken.None);

        html.Should().Be("[rendered:Profiles]");
        source.Calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetRenderedAsync_SecondCall_ServedFromCache()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);

        await service.GetRenderedAsync("Profiles", CancellationToken.None);
        var callsAfterFirst = source.Calls;
        await service.GetRenderedAsync("Profiles", CancellationToken.None);

        source.Calls.Should().Be(callsAfterFirst);
    }

    [Fact]
    public async Task RefreshAllAsync_ClearsAndRefetches()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);
        await service.GetRenderedAsync("Profiles", CancellationToken.None);
        var callsBefore = source.Calls;

        await service.RefreshAllAsync(CancellationToken.None);

        source.Calls.Should().BeGreaterThan(callsBefore);
    }

    [Fact]
    public async Task GetRenderedAsync_UnknownFile_Throws()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);

        var act = async () => await service.GetRenderedAsync("DoesNotExist", CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task GetRenderedAsync_ColdCacheGitHubFailure_ThrowsUnavailable()
    {
        var source = new FakeSource { FailFor = _ => new InvalidOperationException("network down") };
        var service = CreateService(source, out _);

        var act = async () => await service.GetRenderedAsync("Profiles", CancellationToken.None);

        await act.Should().ThrowAsync<GuideContentUnavailableException>();
    }

    [Fact]
    public async Task GetRenderedAsync_WarmCacheThenSourceFails_ServesStale()
    {
        var source = new FakeSource();
        var service = CreateService(source, out var cache);
        await service.GetRenderedAsync("Profiles", CancellationToken.None);

        // Simulate TTL-expired warm cache by clearing only the sentinel, leaving stale entries.
        // Implementation detail: the service tracks a "populated" flag separately from entries.
        source.FailFor = _ => new InvalidOperationException("flaky");
        await service.RefreshAllAsync(CancellationToken.None); // should NOT throw — stale content present

        var html = await service.GetRenderedAsync("Profiles", CancellationToken.None);

        html.Should().Be("[rendered:Profiles]");
    }
}
