using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Humans.Infrastructure.Services.Camps;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// T-06 (2026-05-16 cache-migration plan). Populates the
/// <see cref="CachingCampService"/> dict once at application startup so
/// post-deploy reads hit the cache immediately. Non-fatal: a failed warm-up
/// is logged and the host continues to start; the first user-triggered
/// read will lazily populate via <see cref="CachingCampService.WarmAllAsync"/>.
/// </summary>
public sealed class CampInfoWarmupHostedService : IHostedService
{
    private readonly CachingCampService _cache;
    private readonly ILogger<CampInfoWarmupHostedService> _logger;

    public CampInfoWarmupHostedService(
        CachingCampService cache,
        ILogger<CampInfoWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming CampInfo cache at startup");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _cache.WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "CampInfo cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("CampInfo cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to warm CampInfo cache at startup; lazy reads will populate on demand");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
