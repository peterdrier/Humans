using Humans.Base.Interfaces;

namespace Humans.Finance.Services;

/// <summary>The payment-details owner. Other sections reach the IBAN through <see cref="Contracts.IFinanceService"/>
/// and <see cref="Contracts.IFinanceWrite"/>, which delegate here.</summary>
internal interface IPaymentDetailsManagement : IApplicationService
{
    /// <summary>Normalized and unmasked, or null when none is on record. A merged account's id resolves to the
    /// survivor.</summary>
    Task<string?> GetIbanAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Normalizes, or clears on a blank <paramref name="iban"/>. False when no such user exists.</summary>
    Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default);

    /// <summary>Audited as <c>IbanReveal</c>. Null, and no audit, when none is on record.</summary>
    Task<string?> RevealIbanAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);
}
