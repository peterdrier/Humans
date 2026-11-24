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
    /// The rules as of the last commit changing the folder, cached for a few minutes so the version a buyer is shown is
    /// the one their order records. When GitHub cannot be read, the folder's last good rules (kept without expiry).
    /// Without rules, <see cref="TicketRulesRead.Problem"/> says why. <paramref name="refresh"/> skips the cache, for
    /// checking a folder an admin just set.
    /// </summary>
    Task<TicketRulesRead> GetAsync(string folder, bool refresh = false, CancellationToken ct = default);

    /// <summary>That exact version of the folder on GitHub.</summary>
    Uri Permalink(string folder, string version);
}

/// <summary><paramref name="Version"/> is the commit; <paramref name="Texts"/> maps a language code to its markdown.</summary>
internal sealed record TicketRules(string Folder, string Version, IReadOnlyDictionary<string, string> Texts);

/// <summary>Exactly one of <paramref name="Rules"/> and <paramref name="Problem"/> is set.</summary>
internal sealed record TicketRulesRead(TicketRules? Rules, string? Problem)
{
    public static TicketRulesRead Failed(string problem) => new(null, problem);
}

internal partial class GitHubTicketRulesSource : ITicketRulesSource
{
    private const string CacheKeyPrefix = "tickets:rules:";
    private const string LastGoodKeyPrefix = "tickets:rules:last-good:";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(1);

    // Short, so a hung GitHub fails the buy page over to the last good rules instead of stalling it.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    [GeneratedRegex(@"^(?<name>[^/]+?)(?:-(?<lang>[A-Za-z]{2}))?\.md$", RegexOptions.ExplicitCapture, 1000)]
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
        _client.SetRequestTimeout(RequestTimeout);
        if (!string.IsNullOrEmpty(_settings.AccessToken))
            _client.Credentials = new Credentials(_settings.AccessToken);
    }

    public Uri Permalink(string folder, string version) =>
        new($"https://github.com/{_settings.Owner}/{_settings.Repository}/tree/{version}/{folder}");

    public async Task<TicketRulesRead> GetAsync(string folder, bool refresh = false, CancellationToken ct = default)
    {
        var key = CacheKeyPrefix + folder;
        if (!refresh && _cache.TryGetValue(key, out TicketRulesRead? cached) && cached is not null)
            return cached;

        var lastGoodKey = LastGoodKeyPrefix + folder;
        TicketRulesRead read;
        try
        {
            read = await LoadAsync(folder, ct);
        }
        catch (ApiException ex) when (!ct.IsCancellationRequested)
        {
            // Status code only: the message can carry GitHub's HTML error body.
            _logger.LogWarning("GitHub refused the ticket rules in {Folder}: {StatusCode}; using the last good version", folder, ex.StatusCode);
            return KeepLastGood(key, lastGoodKey, $"GitHub refused the request ({(int)ex.StatusCode}).");
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("GitHub could not be reached for the ticket rules in {Folder}: {Error}; using the last good version", folder, ex.Message);
            return KeepLastGood(key, lastGoodKey, "GitHub could not be reached.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("GitHub timed out reading the ticket rules in {Folder}; using the last good version", folder);
            return KeepLastGood(key, lastGoodKey, "GitHub timed out.");
        }

        if (read.Rules is null)
        {
            _logger.LogWarning("The ticket rules folder {Folder} is unusable: {Problem}", folder, read.Problem);
            _cache.Remove(lastGoodKey);
        }
        else
        {
            _cache.Set(lastGoodKey, read.Rules);
        }

        _cache.Set(key, read, read.Rules is null ? AfterFailure : Fresh);
        return read;
    }

    private TicketRulesRead KeepLastGood(string key, string lastGoodKey, string problem)
    {
        var rules = _cache.Get<TicketRules>(lastGoodKey);
        var read = rules is null ? TicketRulesRead.Failed(problem) : new TicketRulesRead(rules, null);
        _cache.Set(key, read, AfterFailure);
        return read;
    }

    /// <summary>Reads the folder from GitHub. Overridden in tests.</summary>
    internal virtual async Task<TicketRulesRead> LoadAsync(string folder, CancellationToken ct)
    {
        var commits = await _client.Repository.Commit.GetAll(_settings.Owner, _settings.Repository,
            new CommitRequest { Sha = _settings.Branch, Path = folder },
            new ApiOptions { StartPage = 1, PageCount = 1, PageSize = 1 }).WaitAsync(ct);
        if (commits.FirstOrDefault()?.Sha is not { } version)
            return TicketRulesRead.Failed($"{folder} does not exist on the {_settings.Branch} branch.");

        // Everything is read at that commit, so the text always matches the version recorded.
        var files = await _client.Repository.Content.GetAllContentsByRef(
            _settings.Owner, _settings.Repository, folder, version).WaitAsync(ct);
        var (rulesFiles, problem) = RulesFiles(files.Where(f => f.Type == ContentType.File).Select(f => f.Path).ToList());
        if (rulesFiles is null)
            return TicketRulesRead.Failed(problem!);

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, lang) in rulesFiles)
        {
            var bytes = await _client.Repository.Content.GetRawContentByRef(
                _settings.Owner, _settings.Repository, path, version).WaitAsync(ct);
            texts[lang] = Encoding.UTF8.GetString(bytes);
        }

        return new TicketRulesRead(new TicketRules(folder, version, texts), null);
    }

    /// <summary>
    /// The rules' files among a folder's paths, with their language: the one suffix-less file is Spanish and names the
    /// set, and only files sharing its name count. A second suffix-less file (a README beside the rules) is refused,
    /// as it would otherwise read as Spanish.
    /// </summary>
    internal static (IReadOnlyList<(string Path, string Lang)>? Files, string? Problem) RulesFiles(IReadOnlyList<string> paths)
    {
        var candidates = paths
            .Select(path => (Path: path, Match: LanguageFile().Match(path[(path.LastIndexOf('/') + 1)..])))
            .Where(c => c.Match.Success)
            .Select(c => (c.Path, Name: c.Match.Groups["name"].Value,
                Lang: c.Match.Groups["lang"].Success ? c.Match.Groups["lang"].Value.ToLowerInvariant() : "es"))
            .ToList();
        var spanish = candidates.Where(c => string.Equals(c.Lang, "es", StringComparison.Ordinal)).ToList();
        if (spanish.Count == 0)
            return (null, "It has no Spanish file (a .md without a language suffix).");
        if (spanish.Count > 1)
            return (null, $"It has more than one .md without a language suffix: {string.Join(", ", spanish.Select(c => c.Path))}.");
        var set = candidates
            .Where(c => string.Equals(c.Name, spanish[0].Name, StringComparison.Ordinal))
            .DistinctBy(c => c.Lang, StringComparer.Ordinal)
            .Select(c => (c.Path, c.Lang))
            .ToList();
        return set.Exists(c => string.Equals(c.Lang, "en", StringComparison.Ordinal))
            ? (set, null)
            : (null, $"It has no English file ({spanish[0].Name}-en.md).");
    }
}
