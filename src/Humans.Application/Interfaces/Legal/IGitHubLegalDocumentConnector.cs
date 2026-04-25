namespace Humans.Application.Interfaces.Legal;

/// <summary>
/// Thin abstraction over the GitHub content API used by Legal document
/// services. The implementation lives in <c>Humans.Infrastructure</c> and
/// wraps Octokit; the interface is in <c>Humans.Application</c> so Legal
/// services don't take a direct Octokit dependency.
/// </summary>
public interface IGitHubLegalDocumentConnector
{
    /// <summary>
    /// Lists language-keyed file paths in a folder using the
    /// <c>{name}.md → es</c>, <c>{name}-{lang}.md → {lang}</c> convention.
    /// Returns an empty dictionary when the folder is missing. Caller
    /// inspects the dictionary for an <c>"es"</c> entry to determine
    /// whether the canonical file exists.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> DiscoverLanguageFilesAsync(
        string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Fetches raw UTF-8 content plus file SHA for a given repository path.
    /// Returns null when the file does not exist. The Octokit
    /// <c>NotFoundException</c> of the original service is translated to a
    /// null result so Application-layer callers don't depend on Octokit
    /// types.
    /// </summary>
    Task<GitHubFileContent?> GetFileContentAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns the first-line commit message (trimmed, capped at 500 chars)
    /// for a given SHA, or null if the commit cannot be fetched.
    /// </summary>
    Task<string?> GetCommitMessageAsync(string sha, CancellationToken ct = default);

    /// <summary>
    /// Returns the latest commit SHA that touched <paramref name="path"/>
    /// on the configured branch, or null if no commit is found or the
    /// request fails.
    /// </summary>
    Task<string?> GetLatestCommitShaAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Fetches every <c>.md</c> file under <paramref name="folderPath"/>
    /// whose name starts with <paramref name="filePrefix"/>
    /// (case-insensitive) and returns a language-keyed dictionary of file
    /// contents. Language is derived from the <c>{name}-{lang}.md</c>
    /// convention (no suffix ⇒ <c>"es"</c>). Used by the statutes fetcher,
    /// which reads a fixed folder + prefix rather than the dynamic
    /// discover-then-fetch flow used by the sync service.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetFolderContentByPrefixAsync(
        string folderPath, string filePrefix, CancellationToken ct = default);
}

/// <summary>
/// Raw content + SHA of a GitHub repository file fetch.
/// </summary>
public sealed record GitHubFileContent(string Content, string Sha);
