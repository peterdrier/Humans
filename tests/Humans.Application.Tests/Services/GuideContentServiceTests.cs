using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;

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

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            GetMarkdownAsync(fileStem, cancellationToken);

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
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

    [HumansFact]
    public async Task GetRenderedAsync_FirstCall_FetchesFromSource()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);

        var html = await service.GetRenderedAsync("Profiles", Xunit.TestContext.Current.CancellationToken);

        html.Should().Be("[rendered:Profiles]");
        source.Calls.Should().BeGreaterThan(0);
    }

    [HumansFact]
    public async Task GetRenderedAsync_SecondCall_ServedFromCache()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);

        await service.GetRenderedAsync("Profiles", Xunit.TestContext.Current.CancellationToken);
        var callsAfterFirst = source.Calls;
        await service.GetRenderedAsync("Profiles", Xunit.TestContext.Current.CancellationToken);

        source.Calls.Should().Be(callsAfterFirst);
    }

    [HumansFact]
    public async Task RefreshAllAsync_ClearsAndRefetches()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);
        await service.GetRenderedAsync("Profiles", Xunit.TestContext.Current.CancellationToken);
        var callsBefore = source.Calls;

        await service.RefreshAllAsync(Xunit.TestContext.Current.CancellationToken);

        source.Calls.Should().BeGreaterThan(callsBefore);
    }

    [HumansFact]
    public async Task GetRenderedAsync_UnknownFile_Throws()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);

        var act = async () => await service.GetRenderedAsync("DoesNotExist", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [HumansFact]
    public async Task GetRenderedAsync_ColdCacheGitHubFailure_ThrowsUnavailable()
    {
        var source = new FakeSource { FailFor = _ => new InvalidOperationException("network down") };
        var service = CreateService(source, out _);

        var act = async () => await service.GetRenderedAsync("Profiles", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GuideContentUnavailableException>();
    }

    [HumansFact]
    public async Task GetRenderedAsync_WarmCacheThenSourceFails_ServesStale()
    {
        var source = new FakeSource();
        var service = CreateService(source, out _);
        await service.GetRenderedAsync("Profiles", Xunit.TestContext.Current.CancellationToken);

        // Simulate TTL-expired warm cache by clearing only the sentinel, leaving stale entries.
        // Implementation detail: the service tracks a "populated" flag separately from entries.
        source.FailFor = _ => new InvalidOperationException("flaky");
        await service.RefreshAllAsync(Xunit.TestContext.Current.CancellationToken); // should NOT throw — stale content present

        var html = await service.GetRenderedAsync("Profiles", Xunit.TestContext.Current.CancellationToken);

        html.Should().Be("[rendered:Profiles]");
    }
}
