using Humans.SystemSettings.Contracts;
using Humans.SystemSettings.Data;

namespace Humans.SystemSettings.Services;

internal sealed class Service(ISystemSettingsRepository repository) : ISystemSettingsService
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        repository.GetValueAsync(key, cancellationToken);

    public Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default) =>
        repository.SetValueAsync(key, value, cancellationToken);
}
