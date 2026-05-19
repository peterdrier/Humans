using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Periodic job that checks Google Drive Activity API for permission changes
/// not initiated by the system's service account and logs anomalies to the audit log.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class DriveActivityMonitorJob(
    IDriveActivityMonitorService monitorService,
    IHumansMetrics metrics,
    ILogger<DriveActivityMonitorJob> logger,
    IClock clock) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting Drive activity monitor check at {Time}", clock.GetCurrentInstant());

        try
        {
            var anomalyCount = await monitorService.CheckForAnomalousActivityAsync(cancellationToken);

            if (anomalyCount > 0)
            {
                logger.LogWarning("Drive activity monitor completed: {AnomalyCount} anomalous change(s) detected",
                    anomalyCount);
            }
            else
            {
                logger.LogInformation("Drive activity monitor completed: no anomalies detected");
            }

            metrics.RecordJobRun("drive_activity_monitor", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("drive_activity_monitor", "failure");
            logger.LogError(ex, "Error during Drive activity monitor check");
            throw;
        }
    }
}
