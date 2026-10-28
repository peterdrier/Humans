using Humans.Base.Interfaces.Repositories;
using Humans.Settings.Domain;
using NodaTime;

namespace Humans.Settings.Data;

internal interface ISettingsRepository : IRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);

    Task SetValueAsync(string key, string value, CancellationToken ct = default);

    Task<EventSettings?> GetActiveEventSettingsAsync(CancellationToken ct = default);

    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<EventSettings>> GetAllEventSettingsAsync(CancellationToken ct = default);

    /// <summary>Guards the at-most-one-active invariant on write; no DB constraint backs it.</summary>
    Task<bool> AnyOtherActiveEventSettingsAsync(Guid excludingId, CancellationToken ct = default);

    /// <summary>Inserts when the id is unknown, else updates. <paramref name="now"/> stamps <c>UpdatedAt</c>, and
    /// <c>CreatedAt</c> on insert.</summary>
    Task UpsertEventSettingsAsync(EventSettings settings, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Sets <paramref name="id"/> Active and every other Active row Inactive in one save, so a failure leaves the
    /// previous active row in place. Returns the rows it changed, the target last; empty when the id is unknown.
    /// </summary>
    Task<IReadOnlyList<EventSettings>> SetActiveEventSettingsAsync(Guid id, Instant now, CancellationToken ct = default);

    /// <summary>Returns 0 or 1.</summary>
    Task<int> DeleteEventSettingsAsync(Guid id, CancellationToken ct = default);
}
