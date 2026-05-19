using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Scheduled job that reconciles Google resources.
/// Add/remove behavior is controlled by SyncSettings, enforced by the service gateway methods.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class GoogleResourceReconciliationJob(
    IGoogleSyncService googleSyncService,
    IGoogleGroupSync googleGroupSync,
    INotificationService notificationService,
    IHumansMetrics metrics,
    ILogger<GoogleResourceReconciliationJob> logger,
    IClock clock) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting Google resource reconciliation at {Time}", clock.GetCurrentInstant());

        try
        {
            // Reconcile every resource type teams can link. DriveFile is handled by the
            // same Drive permission path as DriveFolder, and omitting it meant soft-deleted
            // teams with linked files kept Google permissions indefinitely because no
            // reconciliation pass ever touched them.
            await googleSyncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, cancellationToken);
            await googleSyncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFile, SyncAction.Execute, cancellationToken);

            // Provisioning of missing Google Groups is handled inside
            // GoogleGroupSyncService.ReconcileAllAsync — when a claim references
            // a group that doesn't yet exist in Google, the reconcile path
            // creates it inline (best-effort) before reconciling membership.
            await googleGroupSync.ReconcileAllAsync(SyncAction.Execute, cancellationToken);

            // Update Drive folder paths (detects renames and moves)
            var pathUpdates = await googleSyncService.UpdateDriveFolderPathsAsync(cancellationToken);
            if (pathUpdates > 0)
            {
                logger.LogInformation("Updated {Count} Drive folder path(s) during reconciliation", pathUpdates);
            }

            // Enforce inherited access restrictions on Drive folders
            var inheritanceCorrected = await googleSyncService.EnforceInheritedAccessRestrictionsAsync(cancellationToken);
            if (inheritanceCorrected > 0)
            {
                logger.LogWarning("Corrected inherited access drift on {Count} Drive folder(s)", inheritanceCorrected);
            }

            // Check Google Group settings for drift and auto-remediate
            var settingsResult = await googleSyncService.CheckGroupSettingsAsync(cancellationToken);
            if (!settingsResult.Skipped && settingsResult.DriftCount > 0)
            {
                logger.LogWarning("Google Group settings drift detected: {DriftCount} group(s) with settings drift out of {Total}",
                    settingsResult.DriftCount, settingsResult.TotalGroups);

                foreach (var report in settingsResult.Reports.Where(r => r.HasDrift))
                {
                    try
                    {
                        var remediation = await googleSyncService.RemediateGroupSettingsAsync(report.GroupEmail, cancellationToken);
                        if (remediation.Succeeded)
                            logger.LogInformation("Auto-remediated settings drift for group '{GroupEmail}'", report.GroupEmail);
                        else
                            logger.LogError("Failed to auto-remediate settings for group '{GroupEmail}': {ErrorMessage}",
                                report.GroupEmail, remediation.ErrorMessage);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to auto-remediate settings for group '{GroupEmail}'", report.GroupEmail);
                    }
                }
            }
            if (settingsResult.ErrorCount > 0)
            {
                logger.LogWarning("Google Group settings check had {ErrorCount} error(s)", settingsResult.ErrorCount);
            }

            // Notify Admin if any drift was detected and corrected
            var totalDrift = inheritanceCorrected + settingsResult.DriftCount;
            if (totalDrift > 0)
            {
                try
                {
                    await notificationService.SendToRoleAsync(
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
                    logger.LogError(ex, "Failed to dispatch GoogleDriftDetected notification");
                }
            }

            metrics.RecordJobRun("google_resource_reconciliation", "success");
            logger.LogInformation("Completed Google resource reconciliation");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("google_resource_reconciliation", "failure");
            logger.LogError(ex, "Error during Google resource reconciliation");
            throw;
        }
    }
}
