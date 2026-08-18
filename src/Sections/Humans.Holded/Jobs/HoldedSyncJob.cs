using Hangfire;
using Humans.Base.Interfaces;
using Humans.Holded.Contracts;

namespace Humans.Holded.Jobs;

/// <summary>Nightly Holded pull: purchase docs (Finance) then the ledger mirror (Holded section).</summary>
/// <remarks>
/// A shim, not the body — the body is <see cref="IHoldedNightlySync"/>, implemented by this
/// section (G5 step 6b, nobodies-collective/Humans#866). It sits under <c>Jobs/</c> because
/// Shell names the concrete type at registration and HUM0034 makes every other public type
/// in a section an error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(IHoldedNightlySync sync) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sync.RunAsync(cancellationToken);
}
