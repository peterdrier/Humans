using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.Application.Tests.Agent;

public class AgentPreloadCorpusBuilderTests
{
    [HumansFact]
    public async Task Tier1_index_lists_only_the_eight_highest_signal_sections()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier1, Xunit.TestContext.Current.CancellationToken);

        text.Should().Contain("**Onboarding**");
        text.Should().Contain("**Teams**");
        text.Should().Contain("**LegalAndConsent**");
        text.Should().Contain("**Governance**");
        text.Should().Contain("**Shifts**");
        text.Should().Contain("**Tickets**");
        text.Should().Contain("**Profiles**");
        text.Should().Contain("**Auth**");
        text.Should().NotContain("**Budget**");
        text.Should().NotContain("**Camps**");
        text.Should().NotContain("**CityPlanning**");
    }

    [HumansFact]
    public async Task Tier2_index_lists_all_fourteen_sections()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);

        text.Should().Contain("**Budget**");
        text.Should().Contain("**Camps**");
        text.Should().Contain("**CityPlanning**");
        text.Should().Contain("**Campaigns**");
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
        return new AgentPreloadCorpusBuilder(reader, community, cache);
    }

    private sealed class StubSource : IGuideContentSource
    {
        public IReadOnlyList<string> CommunityFiles { get; init; } = [];

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                string.Equals(folderPath, CommunityFaqReader.FolderPath, StringComparison.Ordinal)
                    ? $"# {fileStem} title\nLast updated: 2026-02-01\n\n## Overview\nCommunity summary for {fileStem}."
                    : $"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(folderPath, CommunityFaqReader.FolderPath, StringComparison.Ordinal) ? CommunityFiles : []);
    }
}
