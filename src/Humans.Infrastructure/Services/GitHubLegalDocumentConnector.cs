using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.Legal;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Octokit-backed implementation of <see cref="IGitHubLegalDocumentConnector"/>.
/// Keeps the GitHub client surface out of <c>Humans.Application</c> so the
/// Legal document services can live in the Application layer without an
/// Octokit dependency.
/// </summary>
public sealed partial class GitHubLegalDocumentConnector : IGitHubLegalDocumentConnector
{
    // Matches files like "name.md" (canonical es), "name-en.md", "name-de.md"
    [GeneratedRegex(
        @"^(?<name>[A-Za-z0-9_-]+?)(?:-(?<lang>[A-Za-z]{2}))?\.md$",
        RegexOptions.ExplicitCapture,
        1000)]
    private static partial Regex LanguageFilePattern();

    // Looser prefix-based variant used by the statutes fetcher, which matches
    // on a configurable file prefix rather than a fixed base-name.
    [GeneratedRegex(@"^(?<name>.+?)(?:-(?<lang>[A-Za-z]{2}))?\.md$", RegexOptions.None, 1000)]
    private static partial Regex PrefixLanguageFilePattern();

    private readonly GitHubClient _client;
    private readonly GitHubSettings _settings;
    private readonly ILogger<GitHubLegalDocumentConnector> _logger;

    public GitHubLegalDocumentConnector(
        IOptions<GitHubSettings> settings,
        ILogger<GitHubLegalDocumentConnector> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client = new GitHubClient(new ProductHeaderValue("NobodiesHumans"));
        if (!string.IsNullOrEmpty(_settings.AccessToken))
        {
            _client.Credentials = new Credentials(_settings.AccessToken);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> DiscoverLanguageFilesAsync(
        string folderPath, CancellationToken ct = default)
    {
        var languageFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var contents = await _client.Repository.Content.GetAllContentsByRef(
                _settings.Owner,
                _settings.Repository,
                folderPath.TrimEnd('/'),
                _settings.Branch);

            string? canonicalBaseName = null;

            foreach (var item in contents)
            {
                if (item.Type != ContentType.File) continue;

                var match = LanguageFilePattern().Match(item.Name);
                if (!match.Success) continue;

                var baseName = match.Groups["name"].Value;
                var langGroup = match.Groups["lang"];
                var lang = langGroup.Success ? langGroup.Value.ToLowerInvariant() : "es";

                canonicalBaseName ??= baseName;

                if (string.Equals(baseName, canonicalBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    languageFiles[lang] = item.Path;
                }
            }
        }
        catch (NotFoundException)
        {
            _logger.LogWarning("Folder not found in GitHub: {FolderPath}", folderPath);
        }

        return languageFiles;
    }

    public async Task<GitHubFileContent?> GetFileContentAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var contents = await _client.Repository.Content.GetAllContentsByRef(
                _settings.Owner,
                _settings.Repository,
                path,
                _settings.Branch);

            var file = contents.FirstOrDefault();
            if (file is null) return null;

            // Fetch raw content directly — bypasses Base64 encoding issues with non-ASCII content
            var rawBytes = await _client.Repository.Content.GetRawContentByRef(
                _settings.Owner,
                _settings.Repository,
                path,
                _settings.Branch);

            var content = System.Text.Encoding.UTF8.GetString(rawBytes);
            return new GitHubFileContent(content, file.Sha);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<string?> GetCommitMessageAsync(string sha, CancellationToken ct = default)
    {
        try
        {
            var commit = await _client.Repository.Commit.Get(_settings.Owner, _settings.Repository, sha);
            var message = commit.Commit.Message;
            var firstLine = message.Split('\n', 2)[0].Trim();
            return firstLine.Length > 500 ? firstLine[..500] : firstLine;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch commit message for {Sha}", sha);
            return null;
        }
    }

    public async Task<string?> GetLatestCommitShaAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var commits = await _client.Repository.Commit.GetAll(
                _settings.Owner,
                _settings.Repository,
                new CommitRequest { Path = path, Sha = _settings.Branch },
                new ApiOptions { PageCount = 1, PageSize = 1 });
            return commits.FirstOrDefault()?.Sha;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting latest commit SHA for {Path}", path);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetFolderContentByPrefixAsync(
        string folderPath, string filePrefix, CancellationToken ct = default)
    {
        var files = await _client.Repository.Content.GetAllContents(
            _settings.Owner,
            _settings.Repository,
            folderPath);

        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(f =>
            f.Name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase) &&
            f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            var match = PrefixLanguageFilePattern().Match(file.Name);
            if (!match.Success) continue;

            var lang = match.Groups["lang"].Success
                ? match.Groups["lang"].Value.ToLowerInvariant()
                : "es";

            // Fetch full content (GetAllContents for a directory only returns metadata)
            var fileContent = await _client.Repository.Content.GetAllContents(
                _settings.Owner,
                _settings.Repository,
                file.Path);

            if (fileContent.Count > 0 && fileContent[0].Content is not null)
            {
                content[lang] = fileContent[0].Content;
            }
        }

        return content;
    }
}
