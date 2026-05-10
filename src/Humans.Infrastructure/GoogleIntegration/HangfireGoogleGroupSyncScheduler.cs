using Hangfire;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.GoogleIntegration;

public sealed class HangfireGoogleGroupSyncScheduler : IGoogleGroupSyncScheduler
{
    private readonly IBackgroundJobClient _backgroundJobs;

    public HangfireGoogleGroupSyncScheduler(IBackgroundJobClient backgroundJobs)
    {
        _backgroundJobs = backgroundJobs;
    }

    public void Enqueue(string groupKey)
    {
        _backgroundJobs.Enqueue<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None));
    }

    public void Schedule(string groupKey, TimeSpan delay, int retryAttempt)
    {
        _backgroundJobs.Schedule<IGoogleGroupSync>(
            sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, retryAttempt),
            delay);
    }
}
