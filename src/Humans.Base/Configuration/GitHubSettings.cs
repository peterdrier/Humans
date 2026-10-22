namespace Humans.Base.Configuration;

/// <summary>
/// Configuration for GitHub repository containing legal documents.
/// </summary>
public class GitHubSettings
{
    public const string SectionName = "GitHub";

    public string Owner { get; set; } = "nobodies-collective";

    /// <summary>
    /// GitHub repository name containing legal documents.
    /// </summary>
    public string Repository { get; set; } = "legal";

    /// <summary>Optional token for private repos or higher rate limits; unauthenticated access gets 60 requests/hour.</summary>
    public string? AccessToken { get; set; }

    public string Branch { get; set; } = "main";
}
