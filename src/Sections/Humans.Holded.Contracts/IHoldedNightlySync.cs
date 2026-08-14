namespace Humans.Holded.Contracts;

/// <summary>
/// The nightly Holded pull, as one call. Public on the leaf only because its Hangfire target —
/// <c>HoldedSyncJob</c> in <c>Humans.Infrastructure/Jobs</c> — cannot move: Hangfire serializes
/// the declaring type name of a scheduled job, so a queued or retry-delayed run written before a
/// deploy would fail to resolve its target afterwards. The job is the shim; this is the body
/// (G5 step 6b, nobodies-collective/Humans#866).
/// </summary>
public interface IHoldedNightlySync
{
    /// <summary>Purchase docs (Finance) then the ledger mirror (Holded). No-ops without an API key.</summary>
    Task RunAsync(CancellationToken ct = default);
}
