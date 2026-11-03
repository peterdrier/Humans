using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

internal interface IPaymentDetailsManagement : IFinanceService
{
    /// <summary>Audited as <c>IbanReveal</c>. Null, and no audit, when none is on record.</summary>
    Task<string?> RevealIbanAsync(Guid userId, Guid actorUserId, CancellationToken ct = default);
}
