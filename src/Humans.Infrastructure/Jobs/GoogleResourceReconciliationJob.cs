using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Scheduled job that reconciles Google resources.
/// Add/remove behavior is controlled by SyncSettings, enforced by the service gateway methods.
/// </summary>
public class GoogleResourceReconciliationJob : IRecurringJob
{
    private readonly IGoogleSyncService _googleSyncService;
    private readonly INotificationService _notificationService;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<GoogleResourceReconciliationJob> _logger;
    private readonly IClock _clock;

    public GoogleResourceReconciliationJob(
        IGoogleSyncService googleSyncService,
        INotificationService notificationService,
        HumansMetricsService metrics,
        ILogger<GoogleResourceReconciliationJob> logger,
        IClock clock)
    {
        _googleSyncService = googleSyncService;
        _notificationService = notificationService;
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

            // Update Drive folder paths (detects renames and moves)
            var pathUpdates = await _googleSyncService.UpdateDriveFolderPathsAsync(cancellationToken);
            if (pathUpdates > 0)
            {
                _logger.LogInformation("Updated {Count} Drive folder path(s) during reconciliation", pathUpdates);
            }

            // Enforce inherited access restrictions on Drive folders
            var inheritanceCorrected = await _googleSyncService.EnforceInheritedAccessRestrictionsAsync(cancellationToken);
            if (inheritanceCorrected > 0)
            {
                _logger.LogWarning("Corrected inherited access drift on {Count} Drive folder(s)", inheritanceCorrected);
            }

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

            // Notify Admin if any drift was detected and corrected
            var totalDrift = inheritanceCorrected + settingsResult.DriftCount;
            if (totalDrift > 0)
            {
                try
                {
                    await _notificationService.SendToRoleAsync(
                        NotificationSource.GoogleDriftDetected,
                        NotificationClass.Informational,
                        NotificationPriority.Normal,
                        $"Google reconciliation fixed {totalDrift} drift issue(s)",
                        RoleNames.Admin,
                        body: $"Inheritance corrections: {inheritanceCorrected}, group settings drift: {settingsResult.DriftCount}",
                        actionUrl: "/Admin/GoogleSync",
                        actionLabel: "View sync status",
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispatch GoogleDriftDetected notification");
                }
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
