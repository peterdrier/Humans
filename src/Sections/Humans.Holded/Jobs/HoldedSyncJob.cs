using Hangfire;
using Humans.Base.Interfaces;
using Humans.Holded.Services;

namespace Humans.Holded.Jobs;

/// <summary>
/// The "holded-api" lock keeps a run that goes long from overlapping the next one on the rate-limited API.
/// </summary>
[DisableConcurrentExecution("holded-api", 300)]
internal sealed class HoldedSyncJob(IHoldedNightlySync sync) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sync.RunAsync(cancellationToken);
}
