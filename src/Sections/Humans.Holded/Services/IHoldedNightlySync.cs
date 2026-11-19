namespace Humans.Holded.Services;

internal interface IHoldedNightlySync
{
    /// <summary>The nightly refresh: purchase documents, contacts, then the ledger's trailing-window sweep. No-op without
    /// an API key.</summary>
    Task RunAsync(CancellationToken ct = default);
}
