namespace Humans.Application.Interfaces;

/// <summary>
/// Abstracts the GitHub fetch so GuideContentService is testable without network.
/// </summary>
public interface IGuideContentSource
{
    /// <summary>
    /// Fetches the raw markdown for one guide file by stem (e.g. "Profiles" → Profiles.md).
    /// </summary>
    Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default);
}
