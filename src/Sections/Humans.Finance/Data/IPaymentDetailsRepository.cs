using Humans.Base.Interfaces.Repositories;
using Humans.Finance.Domain;
using NodaTime;

namespace Humans.Finance.Data;

internal interface IPaymentDetailsRepository : IRepository
{
    Task<PaymentDetails?> GetAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<PaymentDetails>> GetAllAsync(CancellationToken ct = default);

    /// <summary>A null IBAN records a removal.</summary>
    Task UpsertAsync(Guid userId, string? iban, Instant now, CancellationToken ct = default);

    /// <summary>False when a row existed and nothing was written.</summary>
    Task<bool> TryInsertAsync(Guid userId, string iban, Instant now, CancellationToken ct = default);

    /// <summary>False when there was none.</summary>
    Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Moved when the target has none, deleted when it has its own (the survivor's wins). Idempotent.</summary>
    Task ReassignAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default);
}
