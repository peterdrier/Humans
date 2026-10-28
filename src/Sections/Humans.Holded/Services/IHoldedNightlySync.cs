namespace Humans.Holded.Services;

internal interface IHoldedNightlySync
{
    /// <summary>The ledger mirror's trailing-window sweep. No-op without an API key.</summary>
    Task RunAsync(CancellationToken ct = default);
}
