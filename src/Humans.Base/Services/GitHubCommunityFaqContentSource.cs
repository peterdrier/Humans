using Microsoft.Extensions.Options;
using Octokit;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;

namespace Humans.Base.Services;

/// <summary>
/// <see cref="IGuideContentSource"/> bound to the Community FAQ repo
/// (<see cref="CommunityFaqSettings"/> — default nobodies-collective/knowledge-base@main),
/// kept separate from the code repo so content ships on its own cadence. Folder-scoped:
/// the community reader always passes the folder, so the single-arg overload is unused and
/// throws to make any accidental call obvious.
/// </summary>
public sealed class GitHubCommunityFaqContentSource : IGuideContentSource
{
    private readonly IOptions<CommunityFaqSettings> _settings;
    private readonly GitHubMarkdownReader _reader;

    public GitHubCommunityFaqContentSource(
        IOptions<CommunityFaqSettings> settings,
        IOptions<GitHubSettings> gitHubSettings,
        ILogger<GitHubCommunityFaqContentSource> logger)
    {
        _settings = settings;

        var client = new GitHubClient(new ProductHeaderValue("NobodiesHumansFaq"));
        var token = string.IsNullOrEmpty(settings.Value.AccessToken)
            ? gitHubSettings.Value.AccessToken
            : settings.Value.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            client.Credentials = new Credentials(token);
        }
        _reader = new GitHubMarkdownReader(client, logger);
    }

    public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Community FAQ access is folder-scoped; use the folder-parameterized overload.");

    public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Value;
        return _reader.GetMarkdownAsync(settings.Owner, settings.Repository, settings.Branch,
            folderPath, fileStem, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(
        string folderPath, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Value;
        return _reader.ListMarkdownStemsAsync(settings.Owner, settings.Repository, settings.Branch,
            folderPath, cancellationToken);
    }

    public Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Value;
        return _reader.ListMarkdownPathsAsync(settings.Owner, settings.Repository, settings.Branch, cancellationToken);
    }
}
