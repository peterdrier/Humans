using Humans.Base.Interfaces;

namespace Humans.Google.Contracts;

/// <summary>Setting the identity and recording sync verdicts stay inside Google.</summary>
public interface IGoogleWrite : IApplicationService
{
    /// <summary>
    /// Users calls this after deleting or unlinking the address. When it is the designated identity, the designation
    /// is cleared: reads fall back to the automatic choice.
    /// </summary>
    Task ClearIfAddressAsync(Guid userId, string email, CancellationToken ct = default);
}
