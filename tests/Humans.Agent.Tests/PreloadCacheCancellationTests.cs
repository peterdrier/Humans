using AwesomeAssertions;
using Humans.Agent.Domain;
using Humans.Agent.Services;
using Humans.Agent.Services.Preload;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Agent.Tests;

public class PreloadCacheCancellationTests
{
    public enum Publication { Section, FeatureIndex, FeatureBody, FaqIndex, FaqBody, FaqReload, CorpusBuild, CorpusReload }

    [HumansTheory]
    [InlineData(Publication.Section)]
    [InlineData(Publication.FeatureIndex)]
    [InlineData(Publication.FeatureBody)]
    [InlineData(Publication.FaqIndex)]
    [InlineData(Publication.FaqBody)]
    [InlineData(Publication.FaqReload)]
    [InlineData(Publication.CorpusBuild)]
    [InlineData(Publication.CorpusReload)]
    public async Task CancellationAfterFetch_DoesNotPublishAndTheNextCallRecovers(Publication publication)
    {
        using var cancellation = new CancellationTokenSource();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var source = Substitute.For<IGuideContentSource>();
        var cancel = true;
        void CancelIf(Publication target)
        {
            if (cancel && publication == target) cancellation.Cancel();
        }
        source.GetMarkdownAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var folder = call.ArgAt<string>(0);
            if (string.Equals(folder, CommunityFaqReader.FolderPath, StringComparison.Ordinal))
            {
                CancelIf(Publication.FaqBody);
                CancelIf(Publication.FaqReload);
            }
            else
            {
                CancelIf(Publication.Section);
                CancelIf(Publication.FeatureBody);
            }
            return publication is Publication.CorpusBuild or Publication.CorpusReload
                ? "# Section"
                : cancel ? "# Cancelled content" : "# Recovered";
        });
        source.ListMarkdownPathsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            CancelIf(Publication.FeatureIndex);
            return Task.FromResult<(IReadOnlyList<string>, bool)>((["docs/features/global/example.md"], true));
        });
        source.ListMarkdownStemsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            CancelIf(Publication.FaqIndex);
            return Task.FromResult<IReadOnlyList<string>>(publication is Publication.FaqIndex or Publication.CorpusBuild or Publication.CorpusReload ? [] : ["topic"]);
        });
        var sections = new AgentSectionDocReader(source, cache, NullLogger<AgentSectionDocReader>.Instance);
        var features = new AgentFeatureSpecReader(source, cache, NullLogger<AgentFeatureSpecReader>.Instance);
        var faq = new CommunityFaqReader(source, cache, NullLogger<CommunityFaqReader>.Instance);
        var augmentor = Substitute.For<IAgentPreloadAugmentor>();
        augmentor.BuildFaqMarkdown().Returns(_ =>
        {
            CancelIf(Publication.CorpusBuild);
            CancelIf(Publication.CorpusReload);
            return cancel ? "Cancelled corpus" : "Recovered corpus";
        });
        if (publication is Publication.CorpusBuild or Publication.CorpusReload)
            await faq.ListTopicsAsync(TestContext.Current.CancellationToken);
        var corpus = new AgentPreloadCorpusBuilder(sections, faq, cache, augmentor);

        async Task ReadAsync(CancellationToken ct)
        {
            switch (publication)
            {
                case Publication.Section: await sections.ReadAsync("Shifts", ct); break;
                case Publication.FeatureIndex: await features.KnownStemsAsync(ct); break;
                case Publication.FeatureBody: await features.ReadAsync("example", ct); break;
                case Publication.FaqIndex:
                case Publication.FaqBody: await faq.ListTopicsAsync(ct); break;
                case Publication.FaqReload: await faq.ReloadAsync(ct); break;
                case Publication.CorpusBuild: await corpus.BuildAsync(AgentPreloadConfig.Tier1, ct); break;
                case Publication.CorpusReload: await corpus.ReloadAllAsync(ct); break;
            }
        }

        Func<Task> read = () => ReadAsync(cancellation.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
        cancel = false;
        await ReadAsync(TestContext.Current.CancellationToken);

        if (publication is Publication.Section)
            (await sections.ReadAsync("Shifts", TestContext.Current.CancellationToken)).Should().Be("# Recovered");
        if (publication is Publication.FeatureBody)
            (await features.ReadAsync("example", TestContext.Current.CancellationToken)).Should().Be("# Recovered");
        if (publication is Publication.FaqBody or Publication.FaqReload)
            (await faq.ReadAsync("topic", TestContext.Current.CancellationToken)).Should().Be("# Recovered");
        if (publication is Publication.CorpusBuild or Publication.CorpusReload)
            (await corpus.BuildAsync(AgentPreloadConfig.Tier1, TestContext.Current.CancellationToken)).Should().NotContain("Cancelled corpus");
    }
}
