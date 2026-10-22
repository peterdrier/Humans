
namespace Humans.Consent.Services;

internal interface IGitHubLegalDocumentConnector
{
    /// <summary>
    /// <c>{name}.md</c> → es, <c>{name}-{lang}.md</c> → lang. Empty when the folder is missing; no <c>"es"</c>
    /// entry means no canonical file.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> DiscoverLanguageFilesAsync(
        string folderPath, CancellationToken ct = default);

    /// <summary>Content, blob SHA and the last commit changing the path. Null when the file doesn't exist.</summary>
    Task<GitHubFileContent?> GetFileContentAsync(string path, CancellationToken ct = default);

    /// <summary>First line, trimmed and capped at 500 chars. Null if the commit can't be fetched.</summary>
    Task<string?> GetCommitMessageAsync(string sha, CancellationToken ct = default);

    /// <summary>
    /// Every <c>.md</c> under <paramref name="folderPath"/> whose name starts with <paramref name="filePrefix"/>
    /// (case-insensitive), keyed by language (no suffix ⇒ <c>"es"</c>).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetFolderContentByPrefixAsync(
        string folderPath, string filePrefix, CancellationToken ct = default);
}

internal sealed record GitHubFileContent(string Content, string Sha, string CommitSha);

/// <summary>
/// Keeps Octokit out of the services. <see cref="IsTransient"/>: 5xx and rate limits self-heal on the next run
/// (Warning); auth, scope and config failures repeat and must escalate (Error). The message carries only the
/// status code, never GitHub's HTML error body.
/// </summary>
internal sealed class GitHubApiException : Exception
{
    public System.Net.HttpStatusCode StatusCode { get; }

    public bool IsTransient { get; }

    public GitHubApiException() { }

    public GitHubApiException(string message) : base(message) { }

    public GitHubApiException(string message, Exception inner) : base(message, inner) { }

    public GitHubApiException(System.Net.HttpStatusCode statusCode, bool isTransient, Exception inner)
        : base($"GitHub API error ({(int)statusCode} {statusCode})", inner)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
    }
}
