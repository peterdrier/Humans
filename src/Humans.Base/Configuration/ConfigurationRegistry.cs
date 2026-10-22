using System.Collections.Concurrent;

namespace Humans.Base.Configuration;

/// <summary>
/// Every configuration setting the app touches, registered by the IConfiguration extension methods; the admin
/// Configuration page reads it.
/// </summary>
public sealed class ConfigurationRegistry
{
    private readonly ConcurrentDictionary<string, ConfigurationEntry> _entries = new(StringComparer.Ordinal);

    public void Register(ConfigurationEntry entry)
    {
        _entries.AddOrUpdate(
            entry.Key,
            entry,
            (_, existing) =>
            {
                // The value may change on reload.
                existing.IsSet = entry.IsSet;
                existing.Value = entry.Value;
                return existing;
            });
    }

    /// <summary>Ordered by section, then key.</summary>
    public IReadOnlyList<ConfigurationEntry> GetAll()
    {
        return _entries.Values
            .OrderBy(e => e.Section, StringComparer.Ordinal)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
    }
}
