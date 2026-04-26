using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

public sealed class GitHubGuideContentSource : IGuideContentSource
{
    private readonly IOptions<GuideSettings> _guideSettings;
    private readonly IOptions<GitHubSettings> _gitHubSettings;
    private readonly GitHubClient _client;
    private readonly ILogger<GitHubGuideContentSource> _logger;

    public GitHubGuideContentSource(
        IOptions<GuideSettings> guideSettings,
        IOptions<GitHubSettings> gitHubSettings,
        ILogger<GitHubGuideContentSource> logger)
    {
        _guideSettings = guideSettings;
        _gitHubSettings = gitHubSettings;
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

    public async Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default)
    {
        var settings = _guideSettings.Value;
        var path = $"{settings.FolderPath.TrimEnd('/')}/{fileStem}.md";

        _logger.LogDebug(
            "Fetching guide file {Path} from {Owner}/{Repository}@{Branch}",
            path, settings.Owner, settings.Repository, settings.Branch);

        var rawBytes = await _client.Repository.Content.GetRawContentByRef(
            settings.Owner,
            settings.Repository,
            path,
            settings.Branch);

        return System.Text.Encoding.UTF8.GetString(rawBytes);
    }
}
