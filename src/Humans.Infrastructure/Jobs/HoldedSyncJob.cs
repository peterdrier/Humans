using Hangfire;
using Humans.Application.Interfaces;
using Humans.Holded.Contracts;

namespace Humans.Infrastructure.Jobs;

/// <summary>Nightly Holded pull: purchase docs (Finance) then the ledger mirror (Holded section).</summary>
/// <remarks>
/// A shim, not the body — the body is <see cref="IHoldedNightlySync"/> in
/// <c>src/Sections/Humans.Holded</c> (G5 step 6b, nobodies-collective/Humans#866). This class
/// stays here because Hangfire serializes the declaring type name of a scheduled job: renaming or
/// re-homing it would leave any queued or retry-delayed run unable to resolve its target, and
/// neither the build nor the suite would say so.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(IHoldedNightlySync sync) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sync.RunAsync(cancellationToken);
}
