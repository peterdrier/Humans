using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentPreloadWarmupHostedServiceTests
{
    [HumansFact]
    public async Task WarmCachesAsync_builds_every_tier_and_lists_community_topics()
    {
        var corpus = Substitute.For<IAgentPreloadCorpusBuilder>();
        corpus.BuildAsync(Arg.Any<AgentPreloadConfig>(), Arg.Any<CancellationToken>())
            .Returns("corpus");

        var source = new ListCountingSource();
        var community = new CommunityFaqReader(source, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CommunityFaqReader>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(corpus);
        services.AddSingleton(community);
        await using var sp = services.BuildServiceProvider();

        await AgentPreloadWarmupHostedService.WarmCachesAsync(sp, TestContext.Current.CancellationToken);

        await corpus.Received(1).BuildAsync(AgentPreloadConfig.Tier1, Arg.Any<CancellationToken>());
        await corpus.Received(1).BuildAsync(AgentPreloadConfig.Tier2, Arg.Any<CancellationToken>());
        source.ListCalls.Should().Be(1);
    }

    [HumansFact]
    public async Task WarmAsync_swallows_exceptions()
    {
        var corpus = Substitute.For<IAgentPreloadCorpusBuilder>();
        corpus.BuildAsync(Arg.Any<AgentPreloadConfig>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("github down"));

        var community = new CommunityFaqReader(new ListCountingSource(),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<CommunityFaqReader>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(corpus);
        services.AddSingleton(community);
        await using var sp = services.BuildServiceProvider();
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();

        var svc = new AgentPreloadWarmupHostedService(
            Substitute.For<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
            scopes,
            NullLogger<AgentPreloadWarmupHostedService>.Instance);

        // Must not throw despite the corpus builder failing.
        await svc.WarmAsync();
    }

    private sealed class ListCountingSource : IGuideContentSource
    {
        public int ListCalls { get; private set; }

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\n\n## Overview\nx.");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
