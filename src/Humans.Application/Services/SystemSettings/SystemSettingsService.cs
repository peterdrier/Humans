using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.SystemSettings;

namespace Humans.Application.Services.SystemSettings;

public sealed class SystemSettingsService(ISystemSettingsRepository repository) : ISystemSettingsService
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        repository.GetValueAsync(key, cancellationToken);

    public Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default) =>
        repository.SetValueAsync(key, value, cancellationToken);
}
