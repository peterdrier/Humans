using Humans.Settings.Contracts;
using Humans.Settings.Data;

namespace Humans.Settings.Services;

internal sealed class Service(ISettingsRepository repository) : ISettingsService
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        repository.GetValueAsync(key, cancellationToken);

    public Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default) =>
        repository.SetValueAsync(key, value, cancellationToken);
}
