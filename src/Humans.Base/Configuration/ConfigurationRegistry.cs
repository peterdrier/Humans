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

    /// <summary>For settings read through <c>Environment.GetEnvironmentVariable()</c> rather than IConfiguration.</summary>
    public string? RegisterEnvironmentVariable(
        string envVarName,
        string section,
        bool isSensitive = true,
        ConfigurationImportance importance = ConfigurationImportance.Optional)
    {
        var value = Environment.GetEnvironmentVariable(envVarName);
        var isSet = !string.IsNullOrEmpty(value);

        Register(new ConfigurationEntry
        {
            Section = section,
            Key = $"{envVarName} (env)",
            IsSensitive = isSensitive,
            Importance = importance,
            IsSet = isSet,
            Value = value
        });

        return value;
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
