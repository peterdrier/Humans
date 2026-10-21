using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

/// <summary>
/// The writes other sections make on Finance's member payment details, alongside the read-only
/// <see cref="IFinanceService"/>.
/// </summary>
public interface IFinanceWrite : IApplicationService
{
    /// <summary>Sets the member's IBAN, normalized, or clears it when <paramref name="iban"/> is
    /// blank. The caller validates the format and writes the audit entry, because only it knows on
    /// whose behalf the change is made. Returns false when no such user exists.</summary>
    Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default);
}
