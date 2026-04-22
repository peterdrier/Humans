namespace Humans.Application.Configuration;

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
}
