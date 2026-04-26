using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
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
    private readonly Counter<long> _jobRunsCounter;
    private readonly ILogger<DriveActivityMonitorJob> _logger;
    private readonly IClock _clock;

    public DriveActivityMonitorJob(
        IDriveActivityMonitorService monitorService,
        IMeters meters,
        ILogger<DriveActivityMonitorJob> logger,
        IClock clock)
    {
        _monitorService = monitorService;
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
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

            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "drive_activity_monitor"),
                new KeyValuePair<string, object?>("result", "success"));
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "drive_activity_monitor"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error during Drive activity monitor check");
            throw;
        }
    }
}
