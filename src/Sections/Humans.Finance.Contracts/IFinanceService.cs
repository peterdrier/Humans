using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

/// <summary>Member payment details. Holded reads are on <see cref="IHoldedFinanceService"/>.</summary>
public interface IFinanceService : IApplicationService
{
    /// <summary>Normalized and unmasked, or null when none is on record. A merged account's id resolves to the
    /// survivor. Mask it anywhere outside an admin screen.</summary>
    Task<string?> GetIbanAsync(Guid userId, CancellationToken ct = default);
}
