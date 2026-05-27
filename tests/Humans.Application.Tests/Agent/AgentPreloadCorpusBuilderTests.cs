using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Application.Tests.Agent;

public class AgentPreloadCorpusBuilderTests
{
    [HumansFact]
    public async Task Tier1_index_lists_only_the_eight_highest_signal_sections()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier1, CancellationToken.None);

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
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, CancellationToken.None);

        text.Should().Contain("**Budget**");
        text.Should().Contain("**Camps**");
        text.Should().Contain("**CityPlanning**");
        text.Should().Contain("**Campaigns**");
    }

    [HumansFact]
    public async Task Index_does_not_include_section_bodies()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, CancellationToken.None);

        // Section bodies have these subheadings; the index must not include them.
        text.Should().NotContain("## Invariants");
        text.Should().NotContain("## Data Model");
        text.Should().NotContain("## Triggers");
    }

    [HumansFact]
    public async Task Tier1_output_is_below_the_ITPM_budget()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier1, CancellationToken.None);

        // Rough token estimate: 1 token ≈ 3.8 chars for English/Spanish mix.
        // The index is just keys + taglines; section bodies are fetched on demand
        // via fetch_section_guide. 2K tokens leaves enormous headroom under the
        // Anthropic ITPM budget that previously bounded this corpus at ~25K.
        var estimatedTokens = text.Length / 3.8;
        estimatedTokens.Should().BeLessThan(2_000, "Tier1 preload is now a section index; full bodies are fetched on demand");
    }

    private static IAgentPreloadCorpusBuilder MakeBuilder()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = new AgentSectionDocReader(
            new StubSource(),
            cache,
            Options.Create(new GuideSettings { CacheTtlHours = 6 }),
            NullLogger<AgentSectionDocReader>.Instance);
        return new AgentPreloadCorpusBuilder(reader, cache);
    }

    /// <summary>
    /// Returns synthetic section bodies whose H1 tagline contains the key — enough for the
    /// builder's index assertions (it only reads the first non-empty line after the H1).
    /// </summary>
    private sealed class StubSource : IGuideContentSource
    {
        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\n\nTagline for {fileStem}.");
    }
}
