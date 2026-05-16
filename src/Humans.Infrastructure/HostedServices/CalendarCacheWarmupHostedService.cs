using System.Diagnostics;
using Humans.Infrastructure.Services.Calendar;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// Cache-migration plan task T-08. Populates the
/// <see cref="CachingCalendarService"/> dict at application startup so reads
/// after deploy hit the cache immediately rather than filling in lazily on
/// the first window query.
/// </summary>
/// <remarks>
/// Non-fatal: if warmup fails the error is logged and the host continues to
/// start. The first read will lazily populate via
/// <see cref="CachingCalendarService.WarmAllAsync"/>. Warmup is an
/// optimization, not a correctness requirement.
/// </remarks>
public sealed class CalendarCacheWarmupHostedService : IHostedService
{
    private readonly CachingCalendarService _cache;
    private readonly ILogger<CalendarCacheWarmupHostedService> _logger;

    public CalendarCacheWarmupHostedService(
        CachingCalendarService cache,
        ILogger<CalendarCacheWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming CalendarEventInfo cache at startup");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _cache.WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "CalendarEventInfo cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("CalendarEventInfo cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to warm CalendarEventInfo cache at startup; lazy reads will populate on demand");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
