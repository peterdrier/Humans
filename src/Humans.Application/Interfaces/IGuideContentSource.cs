namespace Humans.Application.Interfaces;

/// <summary>
/// Abstracts a GitHub markdown fetch against the configured Humans repo/branch so callers
/// (GuideContentService, agent doc readers) are testable without network and share one
/// Octokit client + token-resolution path.
/// </summary>
public interface IGuideContentSource
{
    /// <summary>
    /// Fetches the raw markdown for one guide file by stem (e.g. "Profiles" → Profiles.md)
    /// from the configured guide folder (<c>GuideSettings.FolderPath</c>).
    /// </summary>
    Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the raw markdown for one file from an arbitrary folder inside the same
    /// configured Humans repo/branch (e.g. <c>docs/sections</c>, <c>docs/features</c>).
    /// Throws Octokit's <c>NotFoundException</c> when the file is not present; callers
    /// that want null-on-miss must catch it explicitly.
    /// </summary>
    Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the markdown file stems (filename without the <c>.md</c> extension) in a folder
    /// inside the configured Humans repo/branch (e.g. <c>docs/community-kb</c>). Returns an
    /// empty list when the folder is absent. Used for dynamically-discovered corpora whose
    /// file set changes without a code change.
    /// </summary>
    Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default);
}
