namespace Humans.Holded.Contracts;

/// <summary>
/// The nightly Holded pull, as one call. <c>HoldedSyncJob</c> is the Hangfire shim; this is the
/// body. A job is free to move between assemblies: <c>AddOrUpdate&lt;T&gt;(id, …)</c> is keyed on
/// the job id and rewrites the stored type string at every startup.
/// </summary>
public interface IHoldedNightlySync
{
    /// <summary>Purchase docs (Finance) then the ledger mirror (Holded). No-ops without an API key.</summary>
    Task RunAsync(CancellationToken ct = default);
}
