using Humans.Application.Interfaces.Repositories;

namespace Humans.SystemSettings.Data;

/// <summary>
/// Repository for the <c>system_settings</c> table. The interface stays and keeps
/// its prefix (design §6a): <c>RepositoryTests</c> substitutes it.
/// </summary>
internal interface ISystemSettingsRepository : IRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);

    Task SetValueAsync(string key, string value, CancellationToken ct = default);
}
