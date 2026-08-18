using Microsoft.Extensions.Options;
using Octokit;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

public sealed class GitHubGuideContentSource : IGuideContentSource
{
    private readonly IOptions<GuideSettings> _guideSettings;
    private readonly GitHubClient _client;
    private readonly ILogger<GitHubGuideContentSource> _logger;

    public GitHubGuideContentSource(
        IOptions<GuideSettings> guideSettings,
        IOptions<GitHubSettings> gitHubSettings,
        ILogger<GitHubGuideContentSource> logger)
    {
        _guideSettings = guideSettings;
        _logger = logger;

        _client = new GitHubClient(new ProductHeaderValue("NobodiesHumansGuide"));
        // Empty string from env-var wiring is treated the same as null — fall back to GitHubSettings.
        var token = string.IsNullOrEmpty(guideSettings.Value.AccessToken)
            ? gitHubSettings.Value.AccessToken
            : guideSettings.Value.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            _client.Credentials = new Credentials(token);
        }
    }

    public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
        GetMarkdownAsync(_guideSettings.Value.FolderPath, fileStem, cancellationToken);

    public async Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
    {
        var settings = _guideSettings.Value;
        var path = $"{folderPath.TrimEnd('/')}/{fileStem}.md";

        _logger.LogDebug(
            "Fetching markdown file {Path} from {Owner}/{Repository}@{Branch}",
            path, settings.Owner, settings.Repository, settings.Branch);

        var rawBytes = await _client.Repository.Content.GetRawContentByRef(
            settings.Owner,
            settings.Repository,
            path,
            settings.Branch);

        return System.Text.Encoding.UTF8.GetString(rawBytes);
    }

    public async Task<IReadOnlyList<string>> ListMarkdownStemsAsync(
        string folderPath, CancellationToken cancellationToken = default)
    {
        var settings = _guideSettings.Value;
        try
        {
            var contents = await _client.Repository.Content.GetAllContentsByRef(
                settings.Owner,
                settings.Repository,
                folderPath.TrimEnd('/'),
                settings.Branch);

            return contents
                .Where(c => c.Type == ContentType.File &&
                            c.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name[..^3]) // strip ".md"
                .ToList();
        }
        catch (NotFoundException)
        {
            _logger.LogWarning("Folder not found in GitHub: {FolderPath}", folderPath);
            return [];
        }
    }

    public async Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = _guideSettings.Value;
        try
        {
            var tree = await _client.Git.Tree.GetRecursive(
                settings.Owner, settings.Repository, settings.Branch);

            // GitHub caps a recursive tree; past the cap the response is a partial list that
            // looks complete. Say so rather than let a caller treat a truncated corpus as the
            // whole repository (memory/code/always-log-problems.md).
            if (tree.Truncated)
            {
                _logger.LogWarning(
                    "Recursive tree for {Owner}/{Repository}@{Branch} was truncated by GitHub; the markdown listing is incomplete",
                    settings.Owner, settings.Repository, settings.Branch);
            }

            IReadOnlyList<string> paths = [.. tree.Tree
                .Where(i => i.Type == TreeType.Blob &&
                            i.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Path)];
            return (paths, !tree.Truncated);
        }
        catch (NotFoundException)
        {
            _logger.LogWarning(
                "Tree not found in GitHub: {Owner}/{Repository}@{Branch}",
                settings.Owner, settings.Repository, settings.Branch);
            return ([], false);
        }
    }
}
