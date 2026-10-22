using Humans.Base.Interfaces.Repositories;
using Humans.Consent.Domain;
using NodaTime;

namespace Humans.Consent.Data;

internal interface IConsentReminderRepository : IRepository
{
    Task<IReadOnlyList<ConsentReminder>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ConsentReminder>> GetByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task UpsertAsync(Guid userId, Instant sentAt, CancellationToken ct = default);

    /// <summary>False when the member already has a row.</summary>
    Task<bool> TryInsertAsync(Guid userId, Instant sentAt, CancellationToken ct = default);

    Task DeleteAsync(Guid userId, CancellationToken ct = default);
}
