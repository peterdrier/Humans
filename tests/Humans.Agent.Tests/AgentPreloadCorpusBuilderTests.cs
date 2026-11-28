using NSubstitute;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Agent.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Humans.Agent.Services;

namespace Humans.Agent.Tests;

public class AgentPreloadCorpusBuilderTests
{
    /// <summary>
    /// A section the reader can serve but that never appears in the index is one the
    /// agent has no reason to ask for — the index is the only place it learns the key set.
    /// Asserting against <c>KnownSections</c> rather than a hand-listed set keeps the two
    /// lists from drifting apart.
    /// </summary>
    [HumansFact]
    public async Task Build_Index_ListsEverySectionTheReaderServes()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);

        var reader = new AgentSectionDocReader(
            new StubSource(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AgentSectionDocReader>.Instance);

        text.Should().ContainAll(reader.KnownSections.Select(s => $"**{s}**"));
    }

    [HumansFact]
    public async Task Index_Sections_OmitBodies()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);

        // Section bodies have these subheadings; the index must not include them.
        text.Should().NotContain("## Invariants");
        text.Should().NotContain("## Data Model");
        text.Should().NotContain("## Triggers");
    }

    [HumansFact]
    public async Task Index_CommunityFilesExist_IncludesFaqBlock()
    {
        var builder = MakeBuilder(communityFiles: ["FAQ-general"]);
        var text = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);

        text.Should().Contain("Community FAQ");
        text.Should().Contain("**FAQ-general**");
        text.Should().Contain("unofficial");
    }

    [HumansFact]
    public async Task CommunityIndex_Keywords_AreRendered()
    {
        var builder = MakeBuilder(communityFiles: ["FAQ-general"]);
        var text = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);

        text.Should().Contain("covers: kw-FAQ-general, alpha, beta");
    }

    [HumansFact]
    public async Task Index_NoCommunityFiles_OmitsFaqBlock()
    {
        var builder = MakeBuilder(communityFiles: []);
        var text = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);

        text.Should().NotContain("Community FAQ");
    }

    [HumansFact]
    public async Task ReloadAllAsync_NewCommunityFiles_RebuildsCorpus()
    {
        var files = new List<string> { "FAQ-general" };
        var builder = MakeBuilder(communityFiles: files);

        var before = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);
        before.Should().Contain("**FAQ-general**");
        before.Should().NotContain("**FAQ-comms**");

        files.Add("FAQ-comms");
        await builder.ReloadAllAsync(Xunit.TestContext.Current.CancellationToken);

        var after = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);
        after.Should().Contain("**FAQ-comms**");
    }

    [HumansTheory]
    [Xunit.InlineData("topic")]
    [Xunit.InlineData("Onboarding")]
    [Xunit.InlineData("listing")]
    public async Task Corpus_TransientFetchFailure_Recovers(string failingRead)
    {
        var source = new StubSource { CommunityFiles = ["topic"], FailingRead = failingRead };
        var builder = MakeBuilder(source: source);

        var degraded = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);
        degraded.Should().NotContain(string.Equals(failingRead, "Onboarding", StringComparison.Ordinal) ? "**Onboarding**" : "**topic**");
        source.FailingRead = null;
        var recovered = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);
        recovered.Should().ContainAll("**Onboarding**", "**topic**");
        source.FailingRead = "listing";
        (await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken))
            .Should().BeSameAs(recovered, "complete corpora remain cached");
    }

    [HumansTheory]
    [Xunit.InlineData("topic")]
    [Xunit.InlineData("listing")]
    public async Task Reload_Fails_KeepsPreviousCorpus(string failingRead)
    {
        var files = new List<string> { "topic" };
        var source = new StubSource { CommunityFiles = files };
        var builder = MakeBuilder(source: source);
        var original = await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken);
        files.Add("new-topic");
        source.FailingRead = failingRead;

        (await builder.ReloadAllAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeFalse();

        (await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken))
            .Should().BeSameAs(original);
        source.FailingRead = null;
        (await builder.ReloadAllAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeTrue();
        (await builder.BuildAsync(Xunit.TestContext.Current.CancellationToken))
            .Should().Contain("**new-topic**");
    }

    [HumansFact]
    public async Task Build_PendingAfterReload_IsDiscarded()
    {
        var token = Xunit.TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Substitute.For<IGuideContentSource>();
        source.GetMarkdownAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult($"# {call.ArgAt<string>(0)}\n\nSection tagline."));
        source.ListMarkdownStemsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["topic"]));
        source.GetMarkdownAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult($"# {call.ArgAt<string>(1)}\n\nSection tagline."));
        var fetches = 0;
        source.GetMarkdownAsync(CommunityFaqReader.FolderPath, "topic", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++fetches != 1) return Task.FromResult("# Fresh community");
            started.SetResult();
            return pending.Task.WaitAsync(token);
        });
        var builder = MakeBuilder(source: source);

        var oldBuild = builder.BuildAsync(token);
        await started.Task.WaitAsync(token);
        (await builder.ReloadAllAsync(token)).Should().BeTrue();
        pending.SetResult("# Old community");
        await oldBuild.WaitAsync(token);

        (await builder.BuildAsync(token))
            .Should().Contain("Fresh community").And.NotContain("Old community");
    }

    private static IAgentPreloadCorpusBuilder MakeBuilder(IReadOnlyList<string>? communityFiles = null, IGuideContentSource? source = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        source ??= new StubSource { CommunityFiles = communityFiles ?? [] };
        var reader = new AgentSectionDocReader(
            source, cache, NullLogger<AgentSectionDocReader>.Instance);
        var community = new CommunityFaqReader(source, cache, NullLogger<CommunityFaqReader>.Instance);
        return new AgentPreloadCorpusBuilder(reader, community, cache, new StubAugmentor());
    }

    /// <summary>
    /// The augmentor is implemented in Shell and injected through the contracts leaf; these tests
    /// assert the section-index and community-FAQ halves of the corpus, so the four Shell blocks
    /// are stubbed to empty rather than substituted.
    /// </summary>
    private sealed class StubAugmentor : IAgentPreloadAugmentor
    {
        public string BuildAccessMatrixMarkdown() => string.Empty;
        public string BuildGlossariesMarkdown() => string.Empty;
        public string BuildRouteMapMarkdown() => string.Empty;
        public string BuildFaqMarkdown() => string.Empty;
    }

    private sealed class StubSource : IGuideContentSource
    {
        public IReadOnlyList<string> CommunityFiles { get; init; } = [];
        public string? FailingRead { get; set; }

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            string.Equals(FailingRead, fileStem, StringComparison.Ordinal) ? Task.FromException<string>(new IOException("temporary outage")) : Task.FromResult(
                string.Equals(folderPath, CommunityFaqReader.FolderPath, StringComparison.Ordinal)
                    ? $"# {fileStem} title\nLast updated: 2026-02-01\n\n## Overview\nCommunity summary for {fileStem}.\n\n## Keywords\nkw-{fileStem}, alpha, beta"
                    : $"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            string.Equals(FailingRead, "listing", StringComparison.Ordinal) ? Task.FromException<IReadOnlyList<string>>(new IOException("temporary outage")) :
            Task.FromResult(string.Equals(folderPath, CommunityFaqReader.FolderPath, StringComparison.Ordinal) ? CommunityFiles : []);

        public Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<string>, bool)>(([], true));
    }
}
