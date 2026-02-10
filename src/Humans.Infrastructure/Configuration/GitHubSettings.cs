namespace Humans.Infrastructure.Configuration;

/// <summary>
/// Configuration for GitHub repository containing legal documents.
/// </summary>
public class GitHubSettings
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "GitHub";

    /// <summary>
    /// GitHub repository owner (organization or user).
    /// </summary>
    public string Owner { get; set; } = "nobodies-collective";

    /// <summary>
    /// GitHub repository name containing legal documents.
    /// </summary>
    public string Repository { get; set; } = "legal";

    /// <summary>
    /// Optional personal access token for private repos or higher rate limits.
    /// If not provided, uses unauthenticated access (60 requests/hour).
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Branch to sync from.
    /// </summary>
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Grace period (in days) before membership becomes inactive due to missing re-consent.
    /// Defaults to 7 days.
    /// </summary>
    public int ReConsentGracePeriodDays { get; set; } = 7;

    /// <summary>
    /// Document path mappings. Key is DocumentType, value contains paths.
    /// </summary>
    public Dictionary<string, DocumentPathConfig> Documents { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Path configuration for a legal document.
/// </summary>
public class DocumentPathConfig
{
    /// <summary>
    /// Path to Spanish version (canonical/legally binding).
    /// </summary>
    public string SpanishPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to English version (translation).
    /// </summary>
    public string EnglishPath { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name for the document.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this document requires consent from all members.
    /// </summary>
    public bool IsRequired { get; set; } = true;
}
