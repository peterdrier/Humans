using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Monitor.Contracts;

namespace Humans.Monitor.Jobs;

/// <summary>
/// Periodic job that checks Google Drive Activity API for permission changes
/// not initiated by the system's service account and logs anomalies to the audit log.
/// </summary>
/// <remarks>
/// Owner re-measured at the G5 jobs move (nobodies-collective/Humans#866): the only service
/// this job drives is <c>IDriveActivityMonitorService</c>, which Monitor owns — so Monitor,
/// not GoogleIntegration. Public and under <c>Jobs/</c> because Shell names the concrete
/// type at two sites (<c>AddScoped</c> and the recurring roll-call) and HUM0034 allows a
/// section's public types there too.
/// </remarks>
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
