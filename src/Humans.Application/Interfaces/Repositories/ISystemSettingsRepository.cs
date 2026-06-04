using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the shared <c>system_settings</c> table.
/// </summary>
[Section("SystemSettings")]
public interface ISystemSettingsRepository : IRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);

    Task SetValueAsync(string key, string value, CancellationToken ct = default);
}
