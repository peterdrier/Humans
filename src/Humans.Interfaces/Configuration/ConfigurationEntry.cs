namespace Humans.Application.Configuration;

/// <summary>
/// Metadata about a configuration setting, auto-registered when accessed via extension methods.
/// </summary>
public sealed class ConfigurationEntry
{
    /// <summary>Display section (e.g., "Email", "Google Workspace").</summary>
    public required string Section { get; init; }

    /// <summary>Configuration key (e.g., "Email:SmtpHost").</summary>
    public required string Key { get; init; }

    /// <summary>Whether the setting contains sensitive data (secrets, passwords, tokens).</summary>
    public required bool IsSensitive { get; init; }

    /// <summary>Three-tier importance classification.</summary>
    public required ConfigurationImportance Importance { get; init; }

    /// <summary>Whether the setting resolved to a non-empty value at access time.</summary>
    public bool IsSet { get; set; }

    /// <summary>The resolved value. Stored for all settings; display layer handles masking.</summary>
    public string? Value { get; set; }
}
