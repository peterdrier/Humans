using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Humans.Infrastructure.Services.Profiles;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// Populates the <see cref="CachingProfileService"/> dict once at application
/// startup so bulk-read paths
/// (<see cref="CachingProfileService.GetBirthdayProfilesAsync"/>,
/// <see cref="CachingProfileService.GetApprovedProfilesWithLocationAsync"/>,
/// <see cref="CachingProfileService.GetFilteredHumansAsync"/>,
/// <see cref="CachingProfileService.SearchApprovedUsersAsync"/>) return
/// complete results immediately after deploy rather than filling in lazily
/// as each user is accessed.
/// </summary>
/// <remarks>
/// Non-fatal: if warmup fails (DB unreachable, etc.) the error is logged and
/// the host continues to start. The first user-triggered read will lazily
/// populate entries via <see cref="CachingProfileService.GetFullProfileAsync"/>.
/// The warmup is an optimization, not a correctness requirement.
/// </remarks>
public sealed class FullProfileWarmupHostedService : IHostedService
{
    private readonly CachingProfileService _cache;
    private readonly ILogger<FullProfileWarmupHostedService> _logger;

    public FullProfileWarmupHostedService(
        CachingProfileService cache,
        ILogger<FullProfileWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming FullProfile cache at startup");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _cache.WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "FullProfile cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("FullProfile cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            // Do not crash the host. The first lazy read will populate on demand.
            _logger.LogError(
                ex,
                "Failed to warm FullProfile cache at startup; bulk reads may return partial results until individual profiles are accessed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
