using Humans.Base.Interfaces;

namespace Humans.Settings.Contracts;

/// <summary>
/// Settings' full application boundary: the key/value store plus the writes on
/// the app-wide event settings. Cross-section readers take
/// <see cref="ISettingsServiceRead"/>; a section that also writes here (Email's
/// send-pause flag, Monitor's last-run stamp) declares that with
/// <c>[CrossSectionWrite]</c> (memory/architecture/section-read-write-split.md).
/// </summary>
public interface ISettingsService : ISettingsServiceRead, IApplicationService
{
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates the app-wide event settings row identified by
    /// <see cref="EventSettingsInfo.Id"/>.
    /// </summary>
    Task SaveEventSettingsAsync(EventSettingsInfo settings, CancellationToken cancellationToken = default);
}
