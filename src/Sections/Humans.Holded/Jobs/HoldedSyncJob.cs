using Hangfire;
using Humans.Base.Interfaces;
using Humans.Holded.Contracts;

namespace Humans.Holded.Jobs;

/// <summary>Nightly Holded ledger-mirror sweep. Purchase docs are Finance's own job.</summary>
/// <remarks>
/// A shim, not the body — the body is <see cref="IHoldedNightlySync"/>, implemented by this
/// section. It is public, and stays public, because Hangfire needs the concrete type at
/// registration; HUM0034 makes every other public type in a section an error.
/// The lock resource "holded-api" is shared with Finance's HoldedDocSyncJob so the two nightly
/// Holded pulls never overlap on the rate-limited API, even when the 02:45 doc pull runs long.
/// </remarks>
[DisableConcurrentExecution("holded-api", 300)]
public class HoldedSyncJob(IHoldedNightlySync sync) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sync.RunAsync(cancellationToken);
}
