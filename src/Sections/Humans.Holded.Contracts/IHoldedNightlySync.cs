namespace Humans.Holded.Contracts;

/// <summary>
/// The nightly Holded pull, as one call. The job is the shim; this is the body (G5 step 6b,
/// nobodies-collective/Humans#866). Both are now in this section — <c>HoldedSyncJob</c> moved
/// out of <c>Humans.Infrastructure/Jobs</c> into <c>src/Sections/Humans.Holded/Jobs/</c> at G5
/// lane 5b-5, retiring the "Hangfire serializes the declaring type name so the job cannot move"
/// claim this comment used to carry: <c>AddOrUpdate&lt;T&gt;(id, …)</c> is keyed on the job id
/// and rewrites the stored type string at every startup.
/// </summary>
public interface IHoldedNightlySync
{
    /// <summary>Purchase docs (Finance) then the ledger mirror (Holded). No-ops without an API key.</summary>
    Task RunAsync(CancellationToken ct = default);
}
