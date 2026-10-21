using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

/// <summary>Finance-internal surface over <c>finance_payment_details</c>: the admin reveal.</summary>
internal interface IPaymentDetailsManagement : IFinanceService
{
    /// <summary>The member's unmasked IBAN for an Admin, audited as <c>IbanReveal</c>. Null, and no
    /// audit, when none is on record.</summary>
    Task<string?> RevealIbanAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);
}
