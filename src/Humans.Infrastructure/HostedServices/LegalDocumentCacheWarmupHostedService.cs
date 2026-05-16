using System.Diagnostics;
using Humans.Infrastructure.Services.Legal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// T-04. Populates the <see cref="CachingLegalDocumentSyncService"/> global
/// document set once at application startup so the every-page consent-banner
/// read hits the cache immediately after deploy rather than filling lazily on
/// the first request.
/// </summary>
/// <remarks>
/// Non-fatal: warmup failures are logged and the host continues to start.
/// Lazy fill on first read still works — this hosted service exists only to
/// pay the warm cost during startup latency rather than on a user's request.
/// </remarks>
public sealed class LegalDocumentCacheWarmupHostedService : IHostedService
{
    private readonly CachingLegalDocumentSyncService _cache;
    private readonly ILogger<LegalDocumentCacheWarmupHostedService> _logger;

    public LegalDocumentCacheWarmupHostedService(
        CachingLegalDocumentSyncService cache,
        ILogger<LegalDocumentCacheWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming Legal-document cache at startup");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _cache.WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "Legal-document cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Legal-document cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to warm Legal-document cache at startup; lazy reads will populate on demand");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
