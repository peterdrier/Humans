using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// The one Workspace-identity write another section makes: Users tells GoogleIntegration that an
/// address left the member's account. Setting the identity and recording sync verdicts stay
/// inside GoogleIntegration.
/// </summary>
public interface IGoogleIntegrationWrite : IApplicationService
{
    /// <summary>
    /// Called by Users after it deletes or unlinks <paramref name="email"/> from
    /// <paramref name="userId"/>. When that address is the member's designated identity, the
    /// designation is cleared (the read falls back to the automatic choice and never back to the
    /// frozen legacy <c>user_emails.IsGoogle</c> flag). Otherwise a no-op.
    /// </summary>
    Task ClearIfAddressAsync(Guid userId, string email, CancellationToken ct = default);
}
