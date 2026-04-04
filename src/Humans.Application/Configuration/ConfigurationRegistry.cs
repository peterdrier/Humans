using System.Collections.Concurrent;

namespace Humans.Application.Configuration;

/// <summary>
/// Singleton registry that collects metadata about every configuration setting the app touches.
/// Entries are registered automatically when code uses the IConfiguration extension methods
/// (GetRequiredSetting / GetOptionalSetting). The Admin Configuration page reads from this
/// registry instead of a hardcoded list.
/// </summary>
public sealed class ConfigurationRegistry
{
    private readonly ConcurrentDictionary<string, ConfigurationEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Register or update a configuration entry.
    /// </summary>
    public void Register(ConfigurationEntry entry)
    {
        _entries.AddOrUpdate(
            entry.Key,
            entry,
            (_, existing) =>
            {
                // Update runtime state (value may change on reload)
                existing.IsSet = entry.IsSet;
                existing.Value = entry.Value;
                return existing;
            });
    }

    /// <summary>
    /// Get all registered entries, ordered by section then key.
    /// </summary>
    public IReadOnlyList<ConfigurationEntry> GetAll()
    {
        return _entries.Values
            .OrderBy(e => e.Section, StringComparer.Ordinal)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
    }
}
