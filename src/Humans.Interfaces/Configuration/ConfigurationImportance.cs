namespace Humans.Application.Configuration;

/// <summary>
/// Three-tier importance classification for configuration settings.
/// </summary>
public enum ConfigurationImportance
{
    /// <summary>App won't function without this setting.</summary>
    Critical,

    /// <summary>A feature degrades or is disabled without this setting.</summary>
    Recommended,

    /// <summary>Nice-to-have; app works fine without it.</summary>
    Optional
}
