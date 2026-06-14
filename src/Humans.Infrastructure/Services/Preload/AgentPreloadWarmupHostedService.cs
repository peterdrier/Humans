using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>
/// Loads the agent's GitHub-backed caches into RAM once the app is serving, so the first
/// real request never pays the cold-fetch latency. Hooks <c>ApplicationStarted</c> and runs
/// fire-and-forget — it never blocks startup. The caches have no TTL (see the readers and
/// <see cref="AgentPreloadCorpusBuilder"/>), so there is nothing to re-warm: restart (i.e. a
/// release) is the refresh. Building the Tier2 corpus warms every section guide as a side
/// effect; listing community topics warms every community KB file.
/// </summary>
public sealed class AgentPreloadWarmupHostedService(
    IHostApplicationLifetime lifetime,
    IServiceScopeFactory scopes,
    ILogger<AgentPreloadWarmupHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run after startup completes so the host is never blocked on GitHub I/O.
        lifetime.ApplicationStarted.Register(() => _ = WarmAsync());
        return Task.CompletedTask;
    }

    internal async Task WarmAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            await WarmCachesAsync(scope.ServiceProvider, CancellationToken.None);
            logger.LogInformation("Agent preload caches warmed");
        }
        catch (Exception ex)
        {
            // A warm-up miss must never crash the host; the lazy fetch paths still populate
            // the caches on first use. Logged per memory/code/always-log-problems.md.
            logger.LogWarning(ex, "Agent preload warm-up failed; caches will populate lazily on first use");
        }
    }

    internal static async Task WarmCachesAsync(IServiceProvider sp, CancellationToken cancellationToken)
    {
        var corpus = sp.GetRequiredService<IAgentPreloadCorpusBuilder>();
        foreach (var config in Enum.GetValues<AgentPreloadConfig>())
            await corpus.BuildAsync(config, cancellationToken);

        // ListTopicsAsync reads every community doc to build the index, which warms the
        // per-file cache that fetch_community_faq serves from.
        var community = sp.GetRequiredService<CommunityFaqReader>();
        await community.ListTopicsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
