using Hangfire;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Gate.Contracts;

/// <summary>
/// Retention purge for <c>gate_scan_events</c>. Gate scans are attendance /
/// movement data, so they are not kept indefinitely: rows older than
/// <c>Gate:RetentionDays</c> (default 365) are deleted daily. Set the value to
/// 0 or below to disable the purge.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-3
/// (nobodies-collective/Humans#866). It sits under <c>Contracts/</c> because Shell names the
/// concrete type at registration and HUM0034 makes every other public type in a section
/// assembly an error.
/// </remarks>
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
