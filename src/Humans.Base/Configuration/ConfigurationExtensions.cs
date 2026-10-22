using Microsoft.Extensions.Configuration;

namespace Humans.Base.Configuration;

/// <summary>
/// IConfiguration reads that register each key in the ConfigurationRegistry. Use these instead of raw
/// <c>configuration[]</c> / <c>GetValue</c> calls, or the key never reaches the admin Configuration page.
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>Registers the key as required. Null if not set.</summary>
    public static string? GetRequiredSetting(
        this IConfiguration configuration,
        ConfigurationRegistry registry,
        string key,
        string section,
        bool isSensitive = false)
    {
        return GetSetting(configuration, registry, key, section, isSensitive, ConfigurationImportance.Critical);
    }

    /// <summary>Registers the key as optional. Null if not set.</summary>
    public static string? GetOptionalSetting(
        this IConfiguration configuration,
        ConfigurationRegistry registry,
        string key,
        string section,
        bool isSensitive = false,
        ConfigurationImportance importance = ConfigurationImportance.Optional)
    {
        return GetSetting(configuration, registry, key, section, isSensitive, importance);
    }

    /// <summary>Registers the key and returns its typed value, or the default.</summary>
    public static T GetSettingValue<T>(
        this IConfiguration configuration,
        ConfigurationRegistry registry,
        string key,
        string section,
        T defaultValue,
        bool isSensitive = false,
        ConfigurationImportance importance = ConfigurationImportance.Optional)
    {
        var value = configuration.GetValue(key, defaultValue);
        var rawValue = configuration[key];
        var isSet = !string.IsNullOrEmpty(rawValue);

        registry.Register(new ConfigurationEntry
        {
            Section = section,
            Key = key,
            IsSensitive = isSensitive,
            Importance = importance,
            IsSet = isSet,
            Value = isSet ? rawValue : defaultValue?.ToString()
        });

        return value!;
    }

    /// <summary>For settings read through <c>Environment.GetEnvironmentVariable()</c> rather than IConfiguration.</summary>
    public static string? RegisterEnvironmentVariable(
        this ConfigurationRegistry registry,
        string envVarName,
        string section,
        bool isSensitive = true,
        ConfigurationImportance importance = ConfigurationImportance.Optional)
    {
        var value = Environment.GetEnvironmentVariable(envVarName);
        var isSet = !string.IsNullOrEmpty(value);

        registry.Register(new ConfigurationEntry
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

    private static string? GetSetting(
        IConfiguration configuration,
        ConfigurationRegistry registry,
        string key,
        string section,
        bool isSensitive,
        ConfigurationImportance importance)
    {
        var value = configuration[key];
        var isSet = !string.IsNullOrEmpty(value);

        registry.Register(new ConfigurationEntry
        {
            Section = section,
            Key = key,
            IsSensitive = isSensitive,
            Importance = importance,
            IsSet = isSet,
            Value = value
        });

        return value;
    }
}
