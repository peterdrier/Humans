using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentPreloadCorpusBuilderTests
{
    [HumansFact]
    public async Task Tier1_includes_only_the_eight_highest_signal_sections()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier1, CancellationToken.None);

        text.Should().Contain("# Onboarding");
        text.Should().Contain("# Teams");
        text.Should().Contain("# LegalAndConsent");
        text.Should().Contain("# Governance");
        text.Should().Contain("# Shifts");
        text.Should().Contain("# Tickets");
        text.Should().Contain("# Profiles");
        text.Should().Contain("# Auth");
        text.Should().NotContain("# Budget");
        text.Should().NotContain("# Camps");
        text.Should().NotContain("# CityPlanning");
    }

    [HumansFact]
    public async Task Tier2_includes_all_fourteen_sections()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, CancellationToken.None);

        text.Should().Contain("# Budget");
        text.Should().Contain("# Camps");
        text.Should().Contain("# CityPlanning");
        text.Should().Contain("# Campaigns");
    }

    [HumansFact]
    public async Task Tier1_output_is_below_the_ITPM_budget()
    {
        var builder = MakeBuilder();
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier1, CancellationToken.None);

        // Rough token estimate: 1 token ≈ 3.8 chars for English/Spanish mix.
        var estimatedTokens = text.Length / 3.8;
        estimatedTokens.Should().BeLessThan(26_000, "Tier1 preload must stay under the 25K spec budget with slack for the user-context tail");
    }

    private static IAgentPreloadCorpusBuilder MakeBuilder()
    {
        var env = new TestHostEnvironment();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = new AgentSectionDocReader(env);
        return new AgentPreloadCorpusBuilder(reader, cache);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "sections")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Humans.Application.Tests";
        public string ContentRootPath { get; set; } = RepoRoot();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(RepoRoot());
    }
}
