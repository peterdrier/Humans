using Humans.Base.Interfaces.Repositories;

namespace Humans.Settings.Data;

/// <summary>
/// Repository for the <c>settings</c> table. The interface stays and keeps
/// its prefix (design §6a): <c>RepositoryTests</c> substitutes it.
/// </summary>
internal interface ISettingsRepository : IRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);

    Task SetValueAsync(string key, string value, CancellationToken ct = default);
}
