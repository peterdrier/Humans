using Hangfire;
using Humans.Base.Interfaces;
using Humans.Gate.Services;
using Microsoft.Extensions.Configuration;
using NodaTime;

namespace Humans.Gate.Jobs;

/// <summary>Gate scans are attendance data, so they aren't kept indefinitely. <c>Gate:RetentionDays</c> of 0 or less disables the purge.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class GateRetentionJob(
    IGateService gate,
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
        var removed = await gate.PurgeScansBeforeAsync(cutoff, cancellationToken);
        logger.LogInformation(
            "Gate retention purge removed {Count} gate_scan_events older than {Days} days", removed, days);
    }
}
