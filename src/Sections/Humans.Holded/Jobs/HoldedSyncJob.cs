using Hangfire;
using Humans.Base.Interfaces;
using Humans.Holded.Contracts;

namespace Humans.Holded.Jobs;

/// <summary>Nightly Holded pull: purchase docs (Finance) then the ledger mirror (Holded section).</summary>
/// <remarks>
/// A shim, not the body — the body is <see cref="IHoldedNightlySync"/>, implemented by this
/// section. It is public, and stays public, because Hangfire needs the concrete type at
/// registration; HUM0034 makes every other public type in a section an error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(IHoldedNightlySync sync) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sync.RunAsync(cancellationToken);
}
