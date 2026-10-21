using Humans.Base.Interfaces.Repositories;
using Humans.Finance.Domain;
using NodaTime;

namespace Humans.Finance.Data;

/// <summary>The sole reader and writer of <c>finance_payment_details</c>.</summary>
internal interface IPaymentDetailsRepository : IRepository
{
    Task<PaymentDetails?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Inserts the member's row or overwrites its IBAN; a null IBAN records a removal.</summary>
    Task UpsertAsync(Guid userId, string? iban, Instant now, CancellationToken ct = default);

    /// <summary>Deletes the member's row. False when there was none.</summary>
    Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Folds the source's row into the target: moved when the target has none, deleted when
    /// the target has its own (the survivor's account wins). Idempotent.</summary>
    Task ReassignAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default);
}
