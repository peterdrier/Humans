using Hangfire;
using Humans.Application.Interfaces;

namespace Humans.Holded.Contracts;

/// <summary>Nightly Holded pull: purchase docs (Finance) then the ledger mirror (Holded section).</summary>
/// <remarks>
/// A shim, not the body — the body is <see cref="IHoldedNightlySync"/>, implemented by this
/// section (G5 step 6b, nobodies-collective/Humans#866). Moved out of
/// <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-5: the "Hangfire serializes the declaring
/// type name" claim this file used to carry was re-measured and is false —
/// <c>AddOrUpdate&lt;T&gt;(id, …)</c> is keyed on the job id and rewrites the stored type
/// string at every startup. It sits under <c>Contracts/</c> because Shell names the concrete
/// type at registration and HUM0034 makes every other public type in a section an error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(IHoldedNightlySync sync) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sync.RunAsync(cancellationToken);
}
