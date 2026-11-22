using Microsoft.Extensions.Options;
using Octokit;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;

namespace Humans.Base.Services;

public sealed class GitHubGuideContentSource : IGuideContentSource
{
    private readonly IOptions<GuideSettings> _guideSettings;
    private readonly GitHubMarkdownReader _reader;

    public GitHubGuideContentSource(
        IOptions<GuideSettings> guideSettings,
        IOptions<GitHubSettings> gitHubSettings,
        ILogger<GitHubGuideContentSource> logger)
    {
        _guideSettings = guideSettings;

        var client = new GitHubClient(new ProductHeaderValue("NobodiesHumansGuide"));
        // Empty string from env-var wiring is treated the same as null — fall back to GitHubSettings.
        var token = string.IsNullOrEmpty(guideSettings.Value.AccessToken)
            ? gitHubSettings.Value.AccessToken
            : guideSettings.Value.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            client.Credentials = new Credentials(token);
        }
        _reader = new GitHubMarkdownReader(client, logger);
    }

    public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
        GetMarkdownAsync(_guideSettings.Value.FolderPath, fileStem, cancellationToken);

    public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
    {
        var settings = _guideSettings.Value;
        return _reader.GetMarkdownAsync(settings.Owner, settings.Repository, settings.Branch,
            folderPath, fileStem, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(
        string folderPath, CancellationToken cancellationToken = default)
    {
        var settings = _guideSettings.Value;
        return _reader.ListMarkdownStemsAsync(settings.Owner, settings.Repository, settings.Branch,
            folderPath, cancellationToken);
    }

    public Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = _guideSettings.Value;
        return _reader.ListMarkdownPathsAsync(settings.Owner, settings.Repository, settings.Branch, cancellationToken);
    }
}
