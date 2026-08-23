using Humans.Base.Interfaces.Repositories;
using Humans.Settings.Domain;
using NodaTime;

namespace Humans.Settings.Data;

/// <summary>
/// The one repository over the Settings section's two tables —
/// <c>system_settings</c> (key/value) and <c>settings_event</c> (the typed
/// app-wide event settings).
/// The interface stays and keeps its prefix (design §6a): <c>RepositoryTests</c>
/// substitutes it.
/// </summary>
internal interface ISettingsRepository : IRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);

    Task SetValueAsync(string key, string value, CancellationToken ct = default);

    Task<EventSettings?> GetActiveEventSettingsAsync(CancellationToken ct = default);

    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// True when some row other than <paramref name="excludingId"/> is
    /// <c>Active</c>. Guards the at-most-one-active invariant on write; there is
    /// no DB constraint behind it (memory/architecture/no-db-check-constraints.md).
    /// </summary>
    Task<bool> AnyOtherActiveEventSettingsAsync(Guid excludingId, CancellationToken ct = default);

    /// <summary>
    /// Inserts the row when its id is unknown, otherwise updates it in place.
    /// <paramref name="now"/> stamps <c>UpdatedAt</c>, and <c>CreatedAt</c> on insert.
    /// </summary>
    Task UpsertEventSettingsAsync(EventSettings settings, Instant now, CancellationToken ct = default);
}
