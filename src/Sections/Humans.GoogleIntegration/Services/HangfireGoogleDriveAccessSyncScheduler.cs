using Humans.GoogleIntegration.Contracts;
using Hangfire;

namespace Humans.GoogleIntegration.Services;

internal sealed class HangfireGoogleDriveAccessSyncScheduler(
    IBackgroundJobClient backgroundJobs,
    IGoogleDriveActivityClient googleClient,
    ILogger<HangfireGoogleDriveAccessSyncScheduler> logger) : IGoogleDriveAccessSyncScheduler
{
    public void Enqueue(string folderId)
    {
        if (!googleClient.IsConfigured)
        {
            logger.LogInformation(
                "Skipping scoped Google Drive access sync for folder {FolderId} because Google Workspace is not configured",
                folderId);
            return;
        }

        backgroundJobs.Enqueue<IGoogleDriveSync>(
            sync => sync.ReconcileOneAsync(folderId, SyncAction.Execute, CancellationToken.None));
    }
}
