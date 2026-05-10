using System.Diagnostics;
using Humans.Infrastructure.Services.Teams;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// Populates the team read-model dictionary once at application startup.
/// Non-fatal: lazy reads still populate the cache if warmup fails.
/// </summary>
public sealed class TeamsWarmupHostedService : IHostedService
{
    private readonly CachingTeamService _cache;
    private readonly ILogger<TeamsWarmupHostedService> _logger;

    public TeamsWarmupHostedService(
        CachingTeamService cache,
        ILogger<TeamsWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming TeamInfo cache at startup");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _cache.WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "TeamInfo cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("TeamInfo cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to warm TeamInfo cache at startup; first team reads will populate lazily");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
