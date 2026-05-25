using Hangfire;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.GoogleIntegration;

public sealed class HangfireGoogleGroupSyncScheduler(IBackgroundJobClient backgroundJobs) : IGoogleGroupSyncScheduler
{
    // Both call sites bind explicitly to the 4-param ReconcileOneAsync overload
    // so the MethodInfo Hangfire serializes stays stable across changes to the
    // 5-param overload's signature. See IGoogleGroupSync.ReconcileOneAsync.
    public void Enqueue(string groupKey)
    {
        backgroundJobs.Enqueue<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, 0));
    }

    public void Schedule(string groupKey, TimeSpan delay, int retryAttempt)
    {
        backgroundJobs.Schedule<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, retryAttempt),
            delay);
    }
}
