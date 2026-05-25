using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Humans.Application.Constants;
using Humans.Application.Interfaces.Legal;

namespace Humans.Application.Services.Legal;

// Thin GitHub-backed content provider for published legal documents. No DB; inline IMemoryCache.
public sealed class LegalDocumentService(
    IMemoryCache cache,
    IGitHubLegalDocumentConnector gitHub,
    ILogger<LegalDocumentService> logger) : ILegalDocumentService
{
    private static readonly IReadOnlyList<LegalDocumentDefinition> Documents =
    [
        new("statutes", "Statutes", "Estatutos", "ESTATUTOS"),
        new("agent-chat", LegalDocumentNames.AgentChatTerms, "AgentChat", "AGENTCHAT"),
    ];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(30);

    public IReadOnlyList<LegalDocumentDefinition> GetAvailableDocuments() => Documents;

    public async Task<Dictionary<string, string>> GetDocumentContentAsync(string slug)
    {
        var cacheKey = CacheKeys.LegalDocument(slug);
        if (cache.TryGetValue<Dictionary<string, string>>(cacheKey, out var cachedContent) &&
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
                logger.LogWarning("Unknown legal document slug: {Slug}", slug);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var contentDict = await gitHub.GetFolderContentByPrefixAsync(
                definition.RepoFolder, definition.FilePrefix);

            // Mutable copy per signature contract.
            var content = new Dictionary<string, string>(contentDict, StringComparer.Ordinal);
            cache.Set(cacheKey, content, CacheTtl);
            return content;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch legal document {Slug} from GitHub", slug);
            var emptyContent = new Dictionary<string, string>(StringComparer.Ordinal);
            cache.Set(cacheKey, emptyContent, FailureCacheTtl);
            return emptyContent;
        }
    }
}
