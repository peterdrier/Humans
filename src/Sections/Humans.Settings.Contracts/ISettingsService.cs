using Humans.Base.Interfaces;

namespace Humans.Settings.Contracts;

/// <summary>
/// Settings' application boundary: the app-wide key/value store. Callers that
/// write here (Email's send-pause flag, Monitor's last-run stamp) live outside
/// the section, so the write verbs are part of the published surface.
/// </summary>
public interface ISettingsService : IApplicationService
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);
}
