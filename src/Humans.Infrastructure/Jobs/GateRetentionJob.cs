using Hangfire;
using Humans.Application.Interfaces;
using Humans.Gate.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Retention purge for <c>gate_scan_events</c>. Gate scans are attendance /
/// movement data, so they are not kept indefinitely: rows older than
/// <c>Gate:RetentionDays</c> (default 365) are deleted daily. Set the value to
/// 0 or below to disable the purge.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public sealed class GateRetentionJob(
    IGateScanRetention gateScans,
    IConfiguration configuration,
    IClock clock,
    ILogger<GateRetentionJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var days = configuration.GetValue("Gate:RetentionDays", 365);
        if (days <= 0)
        {
            logger.LogInformation("Gate retention purge disabled (Gate:RetentionDays={Days})", days);
            return;
        }

        var cutoff = clock.GetCurrentInstant().Minus(Duration.FromDays(days));
        var removed = await gateScans.PurgeScansBeforeAsync(cutoff, cancellationToken);
        logger.LogInformation(
            "Gate retention purge removed {Count} gate_scan_events older than {Days} days", removed, days);
    }
}
