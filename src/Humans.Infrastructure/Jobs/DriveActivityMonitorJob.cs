using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Periodic job that checks Google Drive Activity API for permission changes
/// not initiated by the system's service account and logs anomalies to the audit log.
/// </summary>
public class DriveActivityMonitorJob : IRecurringJob
{
    private readonly IDriveActivityMonitorService _monitorService;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<DriveActivityMonitorJob> _logger;
    private readonly IClock _clock;

    public DriveActivityMonitorJob(
        IDriveActivityMonitorService monitorService,
        IHumansMetrics metrics,
        ILogger<DriveActivityMonitorJob> logger,
        IClock clock)
    {
        _monitorService = monitorService;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Drive activity monitor check at {Time}", _clock.GetCurrentInstant());

        try
        {
            var anomalyCount = await _monitorService.CheckForAnomalousActivityAsync(cancellationToken);

            if (anomalyCount > 0)
            {
                _logger.LogWarning("Drive activity monitor completed: {AnomalyCount} anomalous change(s) detected",
                    anomalyCount);
            }
            else
            {
                _logger.LogInformation("Drive activity monitor completed: no anomalies detected");
            }

            _metrics.RecordJobRun("drive_activity_monitor", "success");
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("drive_activity_monitor", "failure");
            _logger.LogError(ex, "Error during Drive activity monitor check");
            throw;
        }
    }
}
