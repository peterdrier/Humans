namespace Humans.Application.Interfaces.SystemSettings;

/// <summary>
/// Application boundary for shared key/value system settings.
/// </summary>
public interface ISystemSettingsService : IApplicationService
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);
}
