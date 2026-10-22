using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>Setting the identity and recording sync verdicts stay inside GoogleIntegration.</summary>
public interface IGoogleIntegrationWrite : IApplicationService
{
    /// <summary>
    /// Users calls this after deleting or unlinking the address. When it is the designated identity, the designation
    /// is cleared: reads fall back to the automatic choice, never to the frozen legacy <c>IsGoogle</c> flag.
    /// </summary>
    Task ClearIfAddressAsync(Guid userId, string email, CancellationToken ct = default);
}
