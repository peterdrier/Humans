using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Scheduled job that reconciles Google resources.
/// Add/remove behavior is controlled by SyncSettings, enforced by the service gateway methods.
/// Each top-level phase runs in isolation: a failure in one phase does not abort the others.
/// A summary Admin alert fires via SyncError if any phase fails.
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

        var phaseFailures = new List<string>();
        int inheritanceCorrected = 0;
        var settingsResult = new GroupSettingsDriftResult();

        // Phase 1: Sync Drive folders
        try
        {
            await googleSyncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Google reconciliation phase 'DriveFolder sync' failed");
            phaseFailures.Add("DriveFolder sync");
        }

        // Phase 2: Sync Drive files
        // DriveFile is handled by the same Drive permission path as DriveFolder; omitting it
        // meant soft-deleted teams with linked files kept Google permissions indefinitely.
        try
        {
            await googleSyncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFile, SyncAction.Execute, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Google reconciliation phase 'DriveFile sync' failed");
            phaseFailures.Add("DriveFile sync");
        }

        // Phase 3: Reconcile Google Group membership
        // Provisioning of missing Google Groups is handled inside ReconcileAllAsync — when a
        // claim references a group that doesn't yet exist in Google, the reconcile path creates
        // it inline (best-effort) before reconciling membership.
        try
        {
            await googleGroupSync.ReconcileAllAsync(SyncAction.Execute, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Google reconciliation phase 'Group membership reconcile' failed");
            phaseFailures.Add("Group membership reconcile");
        }

        // Phase 4: Update Drive folder paths (detects renames and moves)
        try
        {
            var pathUpdates = await googleSyncService.UpdateDriveFolderPathsAsync(cancellationToken);
            if (pathUpdates > 0)
            {
                logger.LogInformation("Updated {Count} Drive folder path(s) during reconciliation", pathUpdates);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Google reconciliation phase 'Drive folder path updates' failed");
            phaseFailures.Add("Drive folder path updates");
        }

        // Phase 5: Enforce inherited access restrictions on Drive folders
        try
        {
            inheritanceCorrected = await googleSyncService.EnforceInheritedAccessRestrictionsAsync(cancellationToken);
            if (inheritanceCorrected > 0)
            {
                logger.LogWarning("Corrected inherited access drift on {Count} Drive folder(s)", inheritanceCorrected);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Google reconciliation phase 'Inherited access enforcement' failed");
            phaseFailures.Add("Inherited access enforcement");
        }

        // Phase 6: Check Google Group settings for drift and auto-remediate
        try
        {
            settingsResult = await googleSyncService.CheckGroupSettingsAsync(cancellationToken);
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Failed to auto-remediate settings for group '{GroupEmail}'", report.GroupEmail);
                    }
                }
            }
            if (settingsResult.ErrorCount > 0)
            {
                logger.LogWarning("Google Group settings check had {ErrorCount} error(s)", settingsResult.ErrorCount);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Google reconciliation phase 'Group settings check' failed");
            phaseFailures.Add("Group settings check");
        }

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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to dispatch GoogleDriftDetected notification");
            }
        }

        // Emit a single summary alert if any phase failed; do not re-throw (Hangfire must not
        // mark the whole run as failed when only some phases encountered errors).
        if (phaseFailures.Count > 0)
        {
            var failedPhaseList = string.Join(", ", phaseFailures);
            logger.LogError("Google reconciliation completed with {FailureCount} phase failure(s): {Phases}",
                phaseFailures.Count, failedPhaseList);

            try
            {
                await notificationService.SendToRoleAsync(
                    NotificationSource.SyncError,
                    NotificationClass.Actionable,
                    NotificationPriority.High,
                    $"Google reconciliation: {phaseFailures.Count} phase(s) failed",
                    RoleNames.Admin,
                    body: $"The following phase(s) encountered errors and did not complete: {failedPhaseList}. " +
                          "Other phases ran normally. Check application logs for details.",
                    actionUrl: "/Admin/GoogleSync",
                    actionLabel: "View sync status",
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to dispatch SyncError notification for phase failures");
            }

            metrics.RecordJobRun("google_resource_reconciliation", "partial_failure");
            logger.LogInformation("Completed Google resource reconciliation with phase failures");
        }
        else
        {
            metrics.RecordJobRun("google_resource_reconciliation", "success");
            logger.LogInformation("Completed Google resource reconciliation");
        }
    }
}
