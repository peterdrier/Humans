using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces;

namespace Humans.Application.Services.Legal;

/// <summary>
/// Fetches published legal documents (Statutes) from the configured GitHub
/// repository. This service is a thin content provider with no DB access —
/// it lives in the Application layer after the §15 Legal-document
/// migration so Legal services are uniformly located. GitHub I/O is
/// delegated to <see cref="IGitHubLegalDocumentConnector"/>; caching uses
/// <see cref="IMemoryCache"/> inline (a future caching decorator can
/// replace the inline calls once other sections warrant the pattern here).
/// </summary>
public sealed class LegalDocumentService : ILegalDocumentService
{
    private static readonly IReadOnlyList<LegalDocumentDefinition> Documents =
    [
        new("statutes", "Statutes", "Estatutos", "ESTATUTOS"),
    ];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache;
    private readonly IGitHubLegalDocumentConnector _gitHub;
    private readonly ILogger<LegalDocumentService> _logger;

    public LegalDocumentService(
        IMemoryCache cache,
        IGitHubLegalDocumentConnector gitHub,
        ILogger<LegalDocumentService> logger)
    {
        _cache = cache;
        _gitHub = gitHub;
        _logger = logger;
    }

    public IReadOnlyList<LegalDocumentDefinition> GetAvailableDocuments() => Documents;

    public async Task<Dictionary<string, string>> GetDocumentContentAsync(string slug)
    {
        var cacheKey = CacheKeys.LegalDocument(slug);
        if (_cache.TryGetValue<Dictionary<string, string>>(cacheKey, out var cachedContent) &&
            cachedContent is not null)
        {
            return cachedContent;
        }

        try
        {
            var definition = Documents.FirstOrDefault(d =>
                string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));

            if (definition is null)
            {
                _logger.LogWarning("Unknown legal document slug: {Slug}", slug);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var contentDict = await _gitHub.GetFolderContentByPrefixAsync(
                definition.RepoFolder, definition.FilePrefix);

            // Return a mutable copy for callers (signature contract).
            var content = new Dictionary<string, string>(contentDict, StringComparer.Ordinal);
            _cache.Set(cacheKey, content, CacheTtl);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch legal document {Slug} from GitHub", slug);
            var emptyContent = new Dictionary<string, string>(StringComparer.Ordinal);
            _cache.Set(cacheKey, emptyContent, FailureCacheTtl);
            return emptyContent;
        }
    }
}
