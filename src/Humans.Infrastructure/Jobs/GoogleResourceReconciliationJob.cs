using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Scheduled job that reconciles Google resources.
/// Add/remove behavior is controlled by SyncSettings, enforced by the service gateway methods.
/// </summary>
public class GoogleResourceReconciliationJob
{
    private readonly IGoogleSyncService _googleSyncService;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<GoogleResourceReconciliationJob> _logger;
    private readonly IClock _clock;

    public GoogleResourceReconciliationJob(
        IGoogleSyncService googleSyncService,
        HumansMetricsService metrics,
        ILogger<GoogleResourceReconciliationJob> logger,
        IClock clock)
    {
        _googleSyncService = googleSyncService;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Google resource reconciliation at {Time}", _clock.GetCurrentInstant());

        try
        {
            await _googleSyncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, cancellationToken);
            await _googleSyncService.SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute, cancellationToken);

            // Check Google Group settings for drift and auto-remediate
            var settingsResult = await _googleSyncService.CheckGroupSettingsAsync(cancellationToken);
            if (!settingsResult.Skipped && settingsResult.DriftCount > 0)
            {
                _logger.LogWarning("Google Group settings drift detected: {DriftCount} group(s) with settings drift out of {Total}",
                    settingsResult.DriftCount, settingsResult.TotalGroups);

                foreach (var report in settingsResult.Reports.Where(r => r.HasDrift))
                {
                    try
                    {
                        await _googleSyncService.RemediateGroupSettingsAsync(report.GroupEmail, cancellationToken);
                        _logger.LogInformation("Auto-remediated settings drift for group '{GroupEmail}'", report.GroupEmail);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to auto-remediate settings for group '{GroupEmail}'", report.GroupEmail);
                    }
                }
            }
            if (settingsResult.ErrorCount > 0)
            {
                _logger.LogWarning("Google Group settings check had {ErrorCount} error(s)", settingsResult.ErrorCount);
            }

            _metrics.RecordJobRun("google_resource_reconciliation", "success");
            _logger.LogInformation("Completed Google resource reconciliation");
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("google_resource_reconciliation", "failure");
            _logger.LogError(ex, "Error during Google resource reconciliation");
            throw;
        }
    }
}
