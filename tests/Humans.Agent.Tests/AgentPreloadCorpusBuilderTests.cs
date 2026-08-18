using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Application.Interfaces;
using Humans.Agent.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Humans.Agent.Domain;
using Humans.Agent.Services;

namespace Humans.Agent.Tests;

public class AgentPreloadCorpusBuilderTests
{
    [HumansFact]
    public async Task Tier1_index_lists_only_the_eight_highest_signal_sections()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier1, Xunit.TestContext.Current.CancellationToken);

        text.Should().Contain("**Onboarding**");
        text.Should().Contain("**Teams**");
        text.Should().Contain("**Consent**");
        text.Should().Contain("**Governance**");
        text.Should().Contain("**Shifts**");
        text.Should().Contain("**Tickets**");
        text.Should().Contain("**Users**");
        text.Should().Contain("**Auth**");
        text.Should().NotContain("**Budget**");
        text.Should().NotContain("**Camps**");
        text.Should().NotContain("**CityPlanning**");
    }

    /// <summary>
    /// A section the reader can serve but that never appears in the Tier2 index is one the
    /// agent has no reason to ask for — the index is the only place it learns the key set.
    /// Asserting against <c>KnownSections</c> rather than a hand-listed set keeps the two
    /// lists from drifting apart the way they did before nobodies-collective#951.
    /// </summary>
    [HumansFact]
    public async Task Tier2_index_lists_every_section_the_reader_can_serve()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);

        var reader = new AgentSectionDocReader(
            new StubSource(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AgentSectionDocReader>.Instance);

        text.Should().ContainAll(reader.KnownSections.Select(s => $"**{s}**"));
    }

    [HumansFact]
    public async Task Index_does_not_include_section_bodies()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);

        // Section bodies have these subheadings; the index must not include them.
        text.Should().NotContain("## Invariants");
        text.Should().NotContain("## Data Model");
        text.Should().NotContain("## Triggers");
    }

    [HumansFact]
    public async Task Tier1_output_is_below_the_ITPM_budget()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier1, Xunit.TestContext.Current.CancellationToken);

        // Rough token estimate: 1 token ≈ 3.8 chars for English/Spanish mix.
        // The index is just keys + taglines; section bodies are fetched on demand
        // via fetch_section_guide. 2K tokens leaves enormous headroom under the
        // Anthropic ITPM budget that previously bounded this corpus at ~25K.
        var estimatedTokens = text.Length / 3.8;
        estimatedTokens.Should().BeLessThan(2_000, "Tier1 preload is now a section index; full bodies are fetched on demand");
    }

    [HumansFact]
    public async Task Index_includes_community_faq_block_when_files_exist()
    {
        var builder = MakeBuilder(communityFiles: ["FAQ-general"]);
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);

        text.Should().Contain("Community FAQ");
        text.Should().Contain("**FAQ-general**");
        text.Should().Contain("unofficial");
    }

    [HumansFact]
    public async Task Community_index_renders_covers_keywords_so_the_router_can_match()
    {
        var builder = MakeBuilder(communityFiles: ["FAQ-general"]);
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);

        text.Should().Contain("covers: kw-FAQ-general, alpha, beta");
    }

    [HumansFact]
    public async Task Index_omits_community_block_when_no_files()
    {
        var builder = MakeBuilder(communityFiles: []);
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);

        text.Should().NotContain("Community FAQ");
    }

    [HumansFact]
    public async Task ReloadAllAsync_rebuilds_corpus_with_new_community_files()
    {
        var files = new List<string> { "FAQ-general" };
        var builder = MakeBuilder(communityFiles: files);

        var before = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);
        before.Should().Contain("**FAQ-general**");
        before.Should().NotContain("**FAQ-comms**");

        files.Add("FAQ-comms");
        await builder.ReloadAllAsync(Xunit.TestContext.Current.CancellationToken);

        var after = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);
        after.Should().Contain("**FAQ-comms**");
    }

    private static IAgentPreloadCorpusBuilder MakeBuilder(IReadOnlyList<string>? communityFiles = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var source = new StubSource { CommunityFiles = communityFiles ?? [] };
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

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                string.Equals(folderPath, CommunityFaqReader.FolderPath, StringComparison.Ordinal)
                    ? $"# {fileStem} title\nLast updated: 2026-02-01\n\n## Overview\nCommunity summary for {fileStem}.\n\n## Keywords\nkw-{fileStem}, alpha, beta"
                    : $"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(folderPath, CommunityFaqReader.FolderPath, StringComparison.Ordinal) ? CommunityFiles : []);

        public Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<string>, bool)>(([], true));
    }
}
