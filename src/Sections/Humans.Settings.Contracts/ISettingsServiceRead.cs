using Humans.Base.Interfaces;

namespace Humans.Settings.Contracts;

/// <summary>
/// Settings' cross-section read surface: the app-wide key/value store and the
/// app-wide event settings. Every section outside Settings takes this interface
/// (memory/architecture/section-read-write-split.md); the writes live on
/// <see cref="ISettingsService"/>.
/// </summary>
public interface ISettingsServiceRead : IApplicationService
{
    /// <summary>
    /// The value stored under <paramref name="key"/>, or null when the key has
    /// never been set. Keys are declared on <see cref="SettingKeys"/>.
    /// </summary>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active event settings (invariant: at most one row with
    /// <c>IsActive == true</c>). Null when no event is configured.
    /// </summary>
    Task<EventSettingsInfo?> GetActiveEventSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event settings by id — for rows a section points at by id
    /// (<c>Rota.EventSettingsId</c>, <c>EventGuideSettings.EventSettingsId</c>)
    /// and for historical-cycle reads. Null when the id is unknown.
    /// </summary>
    Task<EventSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
