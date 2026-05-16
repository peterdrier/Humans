using System.Diagnostics;
using Humans.Infrastructure.Services.Tickets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// Populates the ticket read-model projection once at application startup.
/// Non-fatal: lazy reads still populate the projection if warmup fails.
/// </summary>
public sealed class TicketsWarmupHostedService : IHostedService
{
    private readonly CachingTicketQueryService _cache;
    private readonly ILogger<TicketsWarmupHostedService> _logger;

    public TicketsWarmupHostedService(
        CachingTicketQueryService cache,
        ILogger<TicketsWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming TicketOrderInfo cache at startup");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _cache.WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "TicketOrderInfo cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("TicketOrderInfo cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to warm TicketOrderInfo cache at startup; first ticket reads will populate lazily");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
