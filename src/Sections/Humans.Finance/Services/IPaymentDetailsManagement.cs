using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

/// <summary>Finance-internal surface over <c>finance_payment_details</c>: the admin reveal and the
/// legacy copy behind <c>finance.iban-to-payment-details</c>.</summary>
internal interface IPaymentDetailsManagement : IFinanceService
{
    /// <summary>The member's unmasked IBAN for an Admin, audited as <c>IbanReveal</c>. Null, and no
    /// audit, when none is on record.</summary>
    Task<string?> RevealIbanAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Living members whose legacy <c>profiles.Iban</c> has no <c>finance_payment_details</c>
    /// row yet, oldest-joined last.</summary>
    Task<IReadOnlyList<LegacyIbanRow>> GetUncopiedLegacyIbansAsync(CancellationToken ct = default);

    /// <summary>Copies every pending legacy IBAN into its own row. Idempotent: a row written since the
    /// list was read is left alone. Returns the rows written.</summary>
    Task<int> CopyLegacyIbansAsync(CancellationToken ct = default);
}

/// <summary>One pending legacy IBAN. <see cref="Iban"/> is unmasked; the Debug screen masks it.</summary>
internal sealed record LegacyIbanRow(Guid UserId, string BurnerName, string Iban);
