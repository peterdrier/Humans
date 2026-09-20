using Humans.GoogleIntegration.Contracts;
using Hangfire;

namespace Humans.GoogleIntegration.Services;

internal sealed class HangfireGoogleGroupSyncScheduler(
    IBackgroundJobClient backgroundJobs,
    IGoogleDriveActivityClient googleClient,
    ILogger<HangfireGoogleGroupSyncScheduler> logger) : IGoogleGroupSyncScheduler
{
    // Both call sites bind explicitly to the 4-param ReconcileOneAsync overload
    // so the MethodInfo Hangfire serializes stays stable across changes to the
    // 5-param overload's signature. See IGoogleGroupSync.ReconcileOneAsync.
    public void Enqueue(string groupKey)
    {
        if (!googleClient.IsConfigured)
        {
            logger.LogInformation(
                "Skipping scoped Google Group sync for {GroupKey} because Google Workspace is not configured",
                groupKey);
            return;
        }

        backgroundJobs.Enqueue<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, 0));
    }

    public void Schedule(string groupKey, TimeSpan delay, int retryAttempt)
    {
        if (!googleClient.IsConfigured)
        {
            logger.LogInformation(
                "Skipping scoped Google Group sync retry for {GroupKey} because Google Workspace is not configured",
                groupKey);
            return;
        }

        backgroundJobs.Schedule<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, retryAttempt),
            delay);
    }
}
