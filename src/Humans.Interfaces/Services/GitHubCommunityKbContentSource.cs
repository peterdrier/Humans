using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

/// <summary>
/// <see cref="IGuideContentSource"/> bound to the dedicated community knowledge-base repo
/// (<see cref="CommunityKbSettings"/> — default nobodies-collective/knowledge-base@main),
/// kept separate from the code repo so content ships on its own cadence. Folder-scoped:
/// the community reader always passes the folder, so the single-arg overload is unused and
/// throws to make any accidental call obvious.
/// </summary>
public sealed class GitHubCommunityKbContentSource : IGuideContentSource
{
    private readonly IOptions<CommunityKbSettings> _settings;
    private readonly GitHubClient _client;
    private readonly ILogger<GitHubCommunityKbContentSource> _logger;

    public GitHubCommunityKbContentSource(
        IOptions<CommunityKbSettings> settings,
        IOptions<GitHubSettings> gitHubSettings,
        ILogger<GitHubCommunityKbContentSource> logger)
    {
        _settings = settings;
        _logger = logger;

        _client = new GitHubClient(new ProductHeaderValue("NobodiesHumansKb"));
        var token = string.IsNullOrEmpty(settings.Value.AccessToken)
            ? gitHubSettings.Value.AccessToken
            : settings.Value.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            _client.Credentials = new Credentials(token);
        }
    }

    public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Community KB access is folder-scoped; use the folder-parameterized overload.");

    public async Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
    {
        var s = _settings.Value;
        var path = $"{folderPath.TrimEnd('/')}/{fileStem}.md";

        _logger.LogDebug(
            "Fetching community KB file {Path} from {Owner}/{Repository}@{Branch}",
            path, s.Owner, s.Repository, s.Branch);

        var rawBytes = await _client.Repository.Content.GetRawContentByRef(
            s.Owner, s.Repository, path, s.Branch);
        return System.Text.Encoding.UTF8.GetString(rawBytes);
    }

    public async Task<IReadOnlyList<string>> ListMarkdownStemsAsync(
        string folderPath, CancellationToken cancellationToken = default)
    {
        var s = _settings.Value;
        try
        {
            var contents = await _client.Repository.Content.GetAllContentsByRef(
                s.Owner, s.Repository, folderPath.TrimEnd('/'), s.Branch);

            return contents
                .Where(c => c.Type == ContentType.File &&
                            c.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name[..^3])
                .ToList();
        }
        catch (NotFoundException)
        {
            _logger.LogWarning(
                "Community KB folder not found in GitHub: {Owner}/{Repository}@{Branch}/{Folder}",
                s.Owner, s.Repository, s.Branch, folderPath);
            return [];
        }
    }
}
