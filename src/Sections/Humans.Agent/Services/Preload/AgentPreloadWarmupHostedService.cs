using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Humans.Agent.Domain;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Fire-and-forget on <c>ApplicationStarted</c>, so it never blocks startup. The caches have no TTL, so there is
/// nothing to re-warm: a release is the refresh.
/// </summary>
internal sealed class AgentPreloadWarmupHostedService(
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
            await WarmCachesAsync(scope.ServiceProvider, lifetime.ApplicationStopping);
            logger.LogInformation("Agent preload caches warmed");
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            logger.LogInformation("Agent preload warm-up cancelled during shutdown");
        }
        catch (Exception ex)
        {
            // A warm-up miss must never crash the host; the lazy paths still fill the caches on first use.
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
