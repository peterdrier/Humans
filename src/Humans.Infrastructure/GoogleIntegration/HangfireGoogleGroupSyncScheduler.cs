using Hangfire;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.GoogleIntegration;

public sealed class HangfireGoogleGroupSyncScheduler(IBackgroundJobClient backgroundJobs) : IGoogleGroupSyncScheduler
{
    public void Enqueue(string groupKey)
    {
        backgroundJobs.Enqueue<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None));
    }

    public void Schedule(string groupKey, TimeSpan delay, int retryAttempt)
    {
        backgroundJobs.Schedule<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, retryAttempt),
            delay);
    }
}
