using Humans.Base.Interfaces.Repositories;
using Humans.Consent.Domain;
using NodaTime;

namespace Humans.Consent.Data;

/// <summary>
/// The only non-test reader and writer of <c>consent_reminders</c>.
/// </summary>
internal interface IConsentReminderRepository : IRepository
{
    Task<IReadOnlyList<ConsentReminder>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ConsentReminder>> GetByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Inserts or overwrites the member's stamp.</summary>
    Task UpsertAsync(Guid userId, Instant sentAt, CancellationToken ct = default);

    /// <summary>Inserts only when the member has no row; false when one exists.</summary>
    Task<bool> TryInsertAsync(Guid userId, Instant sentAt, CancellationToken ct = default);

    /// <summary>Deletes the member's row, if any.</summary>
    Task DeleteAsync(Guid userId, CancellationToken ct = default);
}
