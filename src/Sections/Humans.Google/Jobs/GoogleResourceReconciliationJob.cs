using Humans.Google.Services;
using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Base.Constants;
using Humans.Google.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Google.Jobs;

/// <summary>
/// Each phase runs in isolation, so one failing does not abort the others; any failure raises one summary alert.
/// Public because Section.cs names it (HUM0034 allows public job types).
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class GoogleResourceReconciliationJob(
    IGoogleSyncManagement googleSyncService,
    IGoogleGroupSync googleGroupSync,
    IGoogleDriveSync googleDriveSync,
    IGoogleDriveActivityClient googleClient,
    INotificationService notificationService,
    IHumansMetrics metrics,
    ILogger<GoogleResourceReconciliationJob> logger,
    IClock clock) : IRecurringJob
{
    private const string GroupSettingsPhase = "Group settings check";

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Without credentials every client is a stub that reports success, so a reconcile would
        // log synced rows while Workspace is untouched.
        if (!googleClient.IsConfigured)
        {
            logger.LogInformation("Skipping Google resource reconciliation because Google Workspace is not configured");
            metrics.RecordJobRun("google_resource_reconciliation", "skipped");
            return;
        }

        logger.LogInformation("Starting Google resource reconciliation at {Time}", clock.GetCurrentInstant());

        var phaseFailures = new List<string>();
        int inheritanceCorrected = 0;
        var groupSettingsCorrected = 0;

        try
        {
            // One Drive pass for Teams and Workgroups, including linked files.
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Per-resource failures come back as diff errors rather than exceptions.
                var driveResult = await googleDriveSync.ReconcileAllAsync(SyncAction.Execute, cancellationToken);
                if (driveResult.ErrorCount > 0)
                {
                    logger.LogError("Google reconciliation phase 'Drive access source reconcile' had {ErrorCount} resource error(s)",
                        driveResult.ErrorCount);
                    phaseFailures.Add("Drive access source reconcile");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Google reconciliation phase 'Drive access source reconcile' failed");
                phaseFailures.Add("Drive access source reconcile");
            }

            // ReconcileAllAsync also creates, best effort, any missing group a claim references.
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupResult = await googleGroupSync.ReconcileAllAsync(SyncAction.Execute, cancellationToken);
                if (groupResult.ErrorCount > 0)
                {
                    logger.LogError("Google reconciliation phase 'Group membership reconcile' had {ErrorCount} resource error(s)",
                        groupResult.ErrorCount);
                    phaseFailures.Add("Group membership reconcile");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Google reconciliation phase 'Group membership reconcile' failed");
                phaseFailures.Add("Group membership reconcile");
            }

            // Detects renames and moves.
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pathUpdates = await googleSyncService.UpdateDriveFolderPathsAsync(cancellationToken);
                if (pathUpdates > 0)
                {
                    logger.LogInformation("Updated {Count} Drive folder path(s) during reconciliation", pathUpdates);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Google reconciliation phase 'Drive folder path updates' failed");
                phaseFailures.Add("Drive folder path updates");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                inheritanceCorrected = await googleSyncService.EnforceInheritedAccessRestrictionsAsync(cancellationToken);
                if (inheritanceCorrected > 0)
                {
                    logger.LogWarning("Corrected inherited access drift on {Count} Drive folder(s)", inheritanceCorrected);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Google reconciliation phase 'Inherited access enforcement' failed");
                phaseFailures.Add("Inherited access enforcement");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var settingsResult = await googleSyncService.CheckGroupSettingsAsync(cancellationToken);
                if (settingsResult.ErrorCount > 0)
                {
                    logger.LogWarning("Google Group settings check had {ErrorCount} error(s)", settingsResult.ErrorCount);
                    if (!phaseFailures.Contains(GroupSettingsPhase))
                        phaseFailures.Add(GroupSettingsPhase);
                }
                if (!settingsResult.Skipped && settingsResult.DriftCount > 0)
                {
                    logger.LogWarning("Google Group settings drift detected: {DriftCount} group(s) with settings drift out of {Total}",
                        settingsResult.DriftCount, settingsResult.TotalGroups);

                    foreach (var report in settingsResult.Reports.Where(r => r.HasDrift))
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var remediation = await googleSyncService.RemediateGroupSettingsAsync(report.GroupEmail, cancellationToken);
                            if (remediation.Succeeded)
                            {
                                groupSettingsCorrected++;
                                logger.LogInformation("Auto-remediated settings drift for group '{GroupEmail}'", report.GroupEmail);
                            }
                            else
                            {
                                if (!phaseFailures.Contains(GroupSettingsPhase))
                                    phaseFailures.Add(GroupSettingsPhase);
                                logger.LogError("Failed to auto-remediate settings for group '{GroupEmail}': {ErrorMessage}",
                                    report.GroupEmail, remediation.ErrorMessage);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                        {
                            if (!phaseFailures.Contains(GroupSettingsPhase))
                                phaseFailures.Add(GroupSettingsPhase);
                            logger.LogError(ex, "Failed to auto-remediate settings for group '{GroupEmail}'", report.GroupEmail);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Google reconciliation phase 'Group settings check' failed");
                if (!phaseFailures.Contains(GroupSettingsPhase))
                    phaseFailures.Add(GroupSettingsPhase);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Google resource reconciliation interrupted by shutdown");
            await ReportAsync(phaseFailures, inheritanceCorrected, groupSettingsCorrected, completed: false);
            throw;
        }

        await ReportAsync(phaseFailures, inheritanceCorrected, groupSettingsCorrected, completed: true);
    }

    private async Task ReportAsync(
        IReadOnlyCollection<string> phaseFailures,
        int inheritanceCorrected,
        int groupSettingsCorrected,
        bool completed)
    {
        // Recorded outcomes must reach admins even when shutdown stops later phases.
        var totalDrift = inheritanceCorrected + groupSettingsCorrected;
        if (totalDrift > 0)
        {
            try
            {
                await notificationService.SendToRoleAsync(
                    GoogleNotificationSources.GoogleDriftDetected,
                    NotificationClass.Informational,
                    NotificationPriority.Normal,
                    $"Google reconciliation fixed {totalDrift} drift issue(s)",
                    RoleNames.Admin,
                    body: $"Inheritance corrections: {inheritanceCorrected}, group settings drift: {groupSettingsCorrected}",
                    actionUrl: "/Google/Sync",
                    actionLabel: "View sync status",
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch GoogleDriftDetected notification");
            }
        }

        // One summary alert and no rethrow: Hangfire must not fail the whole run when only some phases failed.
        if (phaseFailures.Count > 0)
        {
            var failedPhaseList = string.Join(", ", phaseFailures);
            logger.LogError("Google reconciliation recorded {FailureCount} phase failure(s): {Phases}",
                phaseFailures.Count, failedPhaseList);

            try
            {
                await notificationService.SendToRoleAsync(
                    GoogleNotificationSources.SyncError,
                    NotificationClass.Actionable,
                    NotificationPriority.High,
                    $"Google reconciliation: {phaseFailures.Count} phase(s) failed",
                    RoleNames.Admin,
                    body: $"The following phase(s) encountered errors and did not complete: {failedPhaseList}. " +
                          "Check application logs for details.",
                    actionUrl: "/Google/Sync",
                    actionLabel: "View sync status",
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch SyncError notification for phase failures");
            }

            metrics.RecordJobRun("google_resource_reconciliation", "partial_failure");
            if (completed)
                logger.LogInformation("Completed Google resource reconciliation with phase failures");
        }
        else if (completed)
        {
            metrics.RecordJobRun("google_resource_reconciliation", "success");
            logger.LogInformation("Completed Google resource reconciliation");
        }
    }
}
