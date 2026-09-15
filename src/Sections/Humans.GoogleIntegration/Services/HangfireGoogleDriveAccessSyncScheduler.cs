using Humans.GoogleIntegration.Contracts;
using Hangfire;

namespace Humans.GoogleIntegration.Services;

internal sealed class HangfireGoogleDriveAccessSyncScheduler(IBackgroundJobClient backgroundJobs) : IGoogleDriveAccessSyncScheduler
{
    public void Enqueue(string folderId)
    {
        backgroundJobs.Enqueue<IGoogleDriveSync>(
            sync => sync.ReconcileOneAsync(folderId, SyncAction.Execute, CancellationToken.None));
    }
}
