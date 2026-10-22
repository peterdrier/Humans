using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

internal interface IPaymentDetailsManagement : IFinanceService
{
    /// <summary>Audited as <c>IbanReveal</c>. Null, and no audit, when none is on record.</summary>
    Task<string?> RevealIbanAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Living members whose legacy IBAN has no row yet, oldest-joined last.</summary>
    Task<IReadOnlyList<LegacyIbanRow>> GetUncopiedLegacyIbansAsync(CancellationToken ct = default);

    /// <summary>Idempotent: a row written since the list was read is left alone. Returns rows written.</summary>
    Task<int> CopyLegacyIbansAsync(CancellationToken ct = default);
}

/// <summary><see cref="Iban"/> is unmasked; the Debug screen masks it.</summary>
internal sealed record LegacyIbanRow(Guid UserId, string BurnerName, string Iban);
