using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

/// <summary>
/// Finance's member-keyed reads that are not Holded's: the payment details a member is paid to
/// (<c>finance_payment_details</c>). Holded reads stay on <see cref="IHoldedFinanceService"/>.
/// </summary>
public interface IFinanceService : IApplicationService
{
    /// <summary>The member's IBAN, normalized and unmasked, or null when none is on record. A merged
    /// account's id resolves to the surviving account. Callers that show it outside an admin screen
    /// mask it (memory/code/iban-mask-in-logs.md).</summary>
    Task<string?> GetIbanAsync(Guid userId, CancellationToken ct = default);
}
