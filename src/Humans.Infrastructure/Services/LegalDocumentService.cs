using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

public partial class LegalDocumentService : ILegalDocumentService
{
    private static readonly IReadOnlyList<LegalDocumentDefinition> Documents =
    [
        new("statutes", "Statutes", "Estatutos", "ESTATUTOS"),
    ];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    [GeneratedRegex(@"^(?<name>.+?)(?:-(?<lang>[A-Za-z]{2}))?\.md$", RegexOptions.None, 1000)]
    private static partial Regex LanguageFilePattern();

    private readonly IMemoryCache _cache;
    private readonly ILogger<LegalDocumentService> _logger;
    private readonly GitHubSettings _gitHubSettings;

    public LegalDocumentService(
        IMemoryCache cache,
        ILogger<LegalDocumentService> logger,
        IOptions<GitHubSettings> gitHubSettings)
    {
        _cache = cache;
        _logger = logger;
        _gitHubSettings = gitHubSettings.Value;
    }

    public IReadOnlyList<LegalDocumentDefinition> GetAvailableDocuments() => Documents;

    public async Task<Dictionary<string, string>> GetDocumentContentAsync(string slug)
    {
        var cacheKey = CacheKeys.LegalDocument(slug);

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;

            try
            {
                var definition = Documents.FirstOrDefault(d =>
                    string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));

                if (definition is null)
                {
                    _logger.LogWarning("Unknown legal document slug: {Slug}", slug);
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                return await FetchDocumentContentAsync(definition);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch legal document {Slug} from GitHub", slug);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, string>> FetchDocumentContentAsync(LegalDocumentDefinition definition)
    {
        var client = new GitHubClient(new ProductHeaderValue("NobodiesHumans"));
        if (!string.IsNullOrEmpty(_gitHubSettings.AccessToken))
        {
            client.Credentials = new Credentials(_gitHubSettings.AccessToken);
        }

        var files = await client.Repository.Content.GetAllContents(
            _gitHubSettings.Owner,
            _gitHubSettings.Repository,
            definition.RepoFolder);

        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(f =>
            f.Name.StartsWith(definition.FilePrefix, StringComparison.OrdinalIgnoreCase) &&
            f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            var match = LanguageFilePattern().Match(file.Name);
            if (!match.Success) continue;

            var lang = match.Groups["lang"].Success
                ? match.Groups["lang"].Value.ToLowerInvariant()
                : "es";

            // Fetch full content (GetAllContents for a directory only returns metadata)
            var fileContent = await client.Repository.Content.GetAllContents(
                _gitHubSettings.Owner,
                _gitHubSettings.Repository,
                file.Path);

            if (fileContent.Count > 0 && fileContent[0].Content is not null)
            {
                content[lang] = fileContent[0].Content;
            }
        }

        return content;
    }
}
