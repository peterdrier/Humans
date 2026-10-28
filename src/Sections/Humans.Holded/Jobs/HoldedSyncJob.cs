using Hangfire;
using Humans.Base.Interfaces;
using Humans.Holded.Services;

namespace Humans.Holded.Jobs;

/// <summary>
/// The "holded-api" lock is shared with Finance's HoldedDocSyncJob, so the two nightly pulls never overlap on the
/// rate-limited API, even when the 02:45 doc pull runs long.
/// </summary>
[DisableConcurrentExecution("holded-api", 300)]
internal sealed class HoldedSyncJob(IHoldedNightlySync sync) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sync.RunAsync(cancellationToken);
}
