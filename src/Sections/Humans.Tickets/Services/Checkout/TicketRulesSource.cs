using System.Text;
using System.Text.RegularExpressions;
using Humans.Base.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Octokit;

namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// The ticket rules: markdown in one folder of the legal repo (<see cref="GitHubSettings"/>), the same naming as the
/// legal pages: <c>name.md</c> is Spanish, <c>name-en.md</c> English, and so on. Read-only; nothing is signed.
/// </summary>
internal interface ITicketRulesSource
{
    /// <summary>
    /// The rules as of the last commit changing the folder; null when it has no Spanish or no English text, or GitHub
    /// cannot be read. Cached for a few minutes, so the version a buyer is shown is the one their checkout records.
    /// </summary>
    Task<TicketRules?> GetAsync(string folder, CancellationToken ct = default);

    /// <summary>That exact version of the folder on GitHub.</summary>
    Uri Permalink(string folder, string version);
}

/// <summary><paramref name="Version"/> is the commit; <paramref name="Texts"/> maps a language code to its markdown.</summary>
internal sealed record TicketRules(string Folder, string Version, IReadOnlyDictionary<string, string> Texts);

internal sealed partial class GitHubTicketRulesSource : ITicketRulesSource
{
    private const string CacheKeyPrefix = "tickets:rules:";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(1);

    [GeneratedRegex(@"^[^/]+?(?:-(?<lang>[A-Za-z]{2}))?\.md$", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex LanguageFile();

    private readonly GitHubClient _client;
    private readonly GitHubSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GitHubTicketRulesSource> _logger;

    public GitHubTicketRulesSource(
        IOptions<GitHubSettings> settings, IMemoryCache cache, ILogger<GitHubTicketRulesSource> logger)
    {
        _settings = settings.Value;
        _cache = cache;
        _logger = logger;
        _client = new GitHubClient(new ProductHeaderValue("NobodiesHumansTickets"));
        if (!string.IsNullOrEmpty(_settings.AccessToken))
            _client.Credentials = new Credentials(_settings.AccessToken);
    }

    public Uri Permalink(string folder, string version) =>
        new($"https://github.com/{_settings.Owner}/{_settings.Repository}/tree/{version}/{folder}");

    public async Task<TicketRules?> GetAsync(string folder, CancellationToken ct = default)
    {
        var key = CacheKeyPrefix + folder;
        if (_cache.TryGetValue(key, out TicketRules? cached))
            return cached;

        TicketRules? rules = null;
        try
        {
            rules = await LoadAsync(folder, ct);
            if (rules is null)
                _logger.LogWarning("The ticket rules folder {Folder} has no Spanish or no English text", folder);
        }
        catch (ApiException ex) when (!ct.IsCancellationRequested)
        {
            // Status code only: the message can carry GitHub's HTML error body.
            _logger.LogWarning("GitHub refused the ticket rules in {Folder}: {StatusCode}", folder, ex.StatusCode);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("GitHub could not be reached for the ticket rules in {Folder}: {Error}", folder, ex.Message);
        }

        _cache.Set(key, rules, rules is null ? AfterFailure : Fresh);
        return rules;
    }

    private async Task<TicketRules?> LoadAsync(string folder, CancellationToken ct)
    {
        var commits = await _client.Repository.Commit.GetAll(_settings.Owner, _settings.Repository,
            new CommitRequest { Sha = _settings.Branch, Path = folder },
            new ApiOptions { StartPage = 1, PageCount = 1, PageSize = 1 }).WaitAsync(ct);
        if (commits.FirstOrDefault()?.Sha is not { } version)
            return null;

        // Everything is read at that commit, so the text always matches the version recorded.
        var files = await _client.Repository.Content.GetAllContentsByRef(
            _settings.Owner, _settings.Repository, folder, version).WaitAsync(ct);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => f.Type == ContentType.File))
        {
            var match = LanguageFile().Match(file.Name);
            if (!match.Success)
                continue;
            var lang = match.Groups["lang"].Success ? match.Groups["lang"].Value.ToLowerInvariant() : "es";
            if (texts.ContainsKey(lang))
                continue;
            var bytes = await _client.Repository.Content.GetRawContentByRef(
                _settings.Owner, _settings.Repository, file.Path, version).WaitAsync(ct);
            texts[lang] = Encoding.UTF8.GetString(bytes);
        }

        return texts.ContainsKey("es") && texts.ContainsKey("en") ? new TicketRules(folder, version, texts) : null;
    }
}
