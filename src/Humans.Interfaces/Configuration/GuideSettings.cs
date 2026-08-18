namespace Humans.Infrastructure.Configuration;

/// <summary>
/// Configuration for the in-app Guide section. Source location and cache behaviour.
/// </summary>
public class GuideSettings
{
    public const string SectionName = "Guide";

    /// <summary>GitHub owner (defaults to nobodies-collective).</summary>
    public string Owner { get; set; } = "nobodies-collective";

    /// <summary>GitHub repository (defaults to Humans).</summary>
    public string Repository { get; set; } = "Humans";

    /// <summary>Branch to read guide content from.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Folder inside the repo that contains the guide markdown files.</summary>
    public string FolderPath { get; set; } = "docs/guide";

    /// <summary>Cache TTL in hours for rendered guide pages. Sliding expiration.</summary>
    public int CacheTtlHours { get; set; } = 6;

    /// <summary>Optional personal access token. Falls back to GitHubSettings.AccessToken if empty.</summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Returns the raw.githubusercontent.com URL prefix for image references inside
    /// guide content: "https://raw.githubusercontent.com/{Owner}/{Repository}/{Branch}/{FolderPath}/".
    /// </summary>
    public string RawContentBaseUrl =>
        $"https://raw.githubusercontent.com/{Owner}/{Repository}/{Branch}/{FolderPath.TrimEnd('/')}/";
}
