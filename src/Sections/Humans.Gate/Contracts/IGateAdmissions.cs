namespace Humans.Gate.Contracts;

/// <summary>
/// The gate's admit record, read by the section that owns the ticket so a used ticket is never resold. Live, never
/// cached: an admit must stop a resale at once.
/// </summary>
public interface IGateAdmissions
{
    /// <summary>The codes among <paramref name="barcodes"/> the gate has admitted, as passed in.</summary>
    Task<IReadOnlySet<string>> GetAdmittedAsync(IReadOnlyCollection<string> barcodes, CancellationToken ct = default);
}
