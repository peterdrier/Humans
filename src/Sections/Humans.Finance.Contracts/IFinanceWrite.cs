using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

public interface IFinanceWrite : IApplicationService
{
    /// <summary>Normalizes, or clears on a blank <paramref name="iban"/>. The caller validates the format and writes
    /// the audit entry, since only it knows on whose behalf. Returns false when no such user exists.</summary>
    Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default);
}
