using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Humans.Infrastructure.Services.Events;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// T-03 — Populates the <see cref="CachingEventService"/> projections once at
/// application startup so reads after deploy hit the cache immediately rather
/// than filling in lazily on first request.
/// </summary>
/// <remarks>
/// Non-fatal: if warmup fails the error is logged and the host continues to
/// start. The first event-section read will lazily populate the cache via
/// <c>CachingEventService.EnsureLoadedAsync</c>. Warmup is an optimization,
/// not a correctness requirement.
/// </remarks>
public sealed class EventCacheWarmupHostedService : IHostedService
{
    private readonly CachingEventService _cache;
    private readonly ILogger<EventCacheWarmupHostedService> _logger;

    public EventCacheWarmupHostedService(
        CachingEventService cache,
        ILogger<EventCacheWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming Events cache at startup");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _cache.WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "Events cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Events cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to warm Events cache at startup; lazy reads will populate on demand");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
