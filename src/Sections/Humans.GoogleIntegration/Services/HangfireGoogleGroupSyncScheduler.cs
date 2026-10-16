using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Hangfire;

namespace Humans.GoogleIntegration.Services;

internal sealed class HangfireGoogleGroupSyncScheduler(
    IBackgroundJobClient backgroundJobs,
    IGoogleDriveActivityClient googleClient,
    ILogger<HangfireGoogleGroupSyncScheduler> logger) : IGoogleGroupSyncScheduler
{
    public void Enqueue(string groupKey, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        if (!googleClient.IsConfigured)
        {
            logger.LogInformation(
                "Skipping scoped Google Group sync for {GroupKey} because Google Workspace is not configured",
                groupKey);
            return;
        }

        backgroundJobs.Enqueue<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, 0, true, syncSource));
    }

    public void Schedule(string groupKey, TimeSpan delay, int retryAttempt, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        if (!googleClient.IsConfigured)
        {
            logger.LogInformation(
                "Skipping scoped Google Group sync retry for {GroupKey} because Google Workspace is not configured",
                groupKey);
            return;
        }

        backgroundJobs.Schedule<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, retryAttempt, true, syncSource),
            delay);
    }
}
