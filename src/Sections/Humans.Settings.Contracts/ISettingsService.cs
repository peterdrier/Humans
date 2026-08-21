using Humans.Base.Interfaces;

namespace Humans.Settings.Contracts;

/// <summary>
/// Settings' entire cross-section surface: the app-wide key/value store and the
/// app-wide event settings.
/// </summary>
/// <remarks>
/// One interface, no <c>Read</c> suffix. The event settings are written only from
/// inside the section — the <c>/Settings/Admin</c> screen and the carry screen —
/// so <c>SaveEventSettingsAsync</c> is deliberately absent here and lives on the
/// section's own <c>Service</c>. The key/value <see cref="SetValueAsync"/> stays
/// because Email's send-pause flag and Monitor's last-run stamp have always been
/// written from outside; both move to their own sections' settings later.
/// </remarks>
public interface ISettingsService : IApplicationService
{
    /// <summary>
    /// The value stored under <paramref name="key"/>, or null when the key has
    /// never been set. Keys are declared on <see cref="SettingKeys"/>.
    /// </summary>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active event settings (invariant: at most one row with
    /// <see cref="EventSettingsStatus.Active"/>). Null when no event is configured.
    /// </summary>
    Task<EventSettingsInfo?> GetActiveEventSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event settings by id — for rows a section points at by id
    /// (<c>Rota.EventSettingsId</c>, <c>EventGuideSettings.EventSettingsId</c>)
    /// and for historical-cycle reads. Null when the id is unknown.
    /// </summary>
    Task<EventSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
