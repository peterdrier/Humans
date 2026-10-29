using Humans.Base.Interfaces;

namespace Humans.Settings.Contracts;

/// <summary>
/// No <c>SaveEventSettingsAsync</c> here: only the section's own <c>/Settings/Admin</c> POST writes event settings.
/// <see cref="SetValueAsync"/> stays because some keys are written from outside the section.
/// </summary>
public interface ISettingsService : IApplicationService
{
    /// <summary>Null when the key has never been set. Keys are declared on <see cref="SettingKeys"/>.</summary>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Null when no event is active.</summary>
    Task<EventSettingsInfo?> GetActiveEventSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Includes inactive cycles. Null when the id is unknown.</summary>
    Task<EventSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every row, Deleted included, unordered. Public so sections re-keying their own year-based data onto event ids
    /// can map every cycle, not only the active one.
    /// </summary>
    Task<IReadOnlyList<EventSettingsInfo>> GetAllEventSettingsAsync(CancellationToken cancellationToken = default);
}
