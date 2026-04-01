using Microsoft.Extensions.Configuration;

namespace Humans.Application.Configuration;

/// <summary>
/// Extension methods on IConfiguration that auto-register accessed keys in the ConfigurationRegistry.
/// All config consumers should use these instead of raw configuration[] / GetValue calls.
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Get a required configuration setting. Registers the key in the ConfigurationRegistry.
    /// Returns the value or null if not set.
    /// </summary>
    public static string? GetRequiredSetting(
        this IConfiguration configuration,
        ConfigurationRegistry registry,
        string key,
        string section,
        bool isSensitive = false)
    {
        return GetSetting(configuration, registry, key, section, isSensitive, ConfigurationImportance.Critical);
    }

    /// <summary>
    /// Get an optional configuration setting. Registers the key in the ConfigurationRegistry.
    /// Returns the value or null if not set.
    /// </summary>
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

    /// <summary>
    /// Get a configuration setting with a typed default value. Registers the key in the ConfigurationRegistry.
    /// </summary>
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
            Value = isSensitive ? null : (isSet ? rawValue : defaultValue?.ToString())
        });

        return value!;
    }

    /// <summary>
    /// Register an environment variable in the ConfigurationRegistry.
    /// Use for settings read via Environment.GetEnvironmentVariable() rather than IConfiguration.
    /// </summary>
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
            Value = isSensitive ? null : value
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
            Value = isSensitive ? null : value
        });

        return value;
    }
}
