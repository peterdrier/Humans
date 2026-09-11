using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Base.Configuration;

namespace Humans.Guide.Services;

internal sealed class GuideContentService(
    IGuideContentSource source,
    IGuideRenderer renderer,
    IMemoryCache cache,
    IOptions<GuideSettings> settings,
    ILogger<GuideContentService> logger) : IGuideContentService
{
    private const string CacheKeyPrefix = "guide:";

    private readonly TrackedLock _refreshLock = new("GuideContentService.Refresh");

    public async Task<string> GetPageAsync(
        string fileStem,
        GuideRoleContext roleContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);
        ArgumentNullException.ThrowIfNull(roleContext);

        if (!GuideFiles.TryCanonical(fileStem, out var canonical))
        {
            throw new FileNotFoundException($"Guide file '{fileStem}' is not in the known set.");
        }

        var document = await GetDocumentAsync(canonical, cancellationToken);
        return renderer.Render(GuideFilter.Apply(document, roleContext), canonical);
    }

    public Task RefreshAllAsync(CancellationToken cancellationToken = default) =>
        PopulateAsync(isRefresh: true, cancellationToken);

    private async Task<GuideDocument> GetDocumentAsync(string canonical, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey(canonical), out GuideDocument? cached) && cached is not null)
        {
            return cached;
        }

        await PopulateAsync(isRefresh: false, cancellationToken);

        if (cache.TryGetValue(CacheKey(canonical), out GuideDocument? afterPopulate) && afterPopulate is not null)
        {
            return afterPopulate;
        }

        throw new GuideContentUnavailableException(
            $"Guide content '{canonical}' is not currently available.");
    }

    private async Task PopulateAsync(bool isRefresh, CancellationToken cancellationToken)
    {
        using var gate = await _refreshLock.AcquireAsync(logger, cancellationToken);

        var hasStale = GuideFiles.All.Any(s => cache.TryGetValue(CacheKey(s), out GuideDocument? _));

        var ttl = TimeSpan.FromHours(Math.Max(1, settings.Value.CacheTtlHours));
        var anyFailures = false;
        var newEntries = new Dictionary<string, GuideDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var stem in GuideFiles.All)
        {
            try
            {
                var markdown = await source.GetMarkdownAsync(stem, cancellationToken);
                newEntries[stem] = GuideSegmenter.Segment(markdown);
            }
            catch (Exception ex)
            {
                anyFailures = true;
                logger.LogWarning(ex,
                    "Failed to fetch or segment guide file {FileStem}; {Outcome}",
                    stem,
                    hasStale ? "keeping stale cached copy" : "no stale copy available");
            }
        }

        if (!hasStale && newEntries.Count == 0)
        {
            throw new GuideContentUnavailableException(
                "Guide content is unavailable and the cache is cold.");
        }

        foreach (var (stem, document) in newEntries)
        {
            cache.Set(CacheKey(stem), document, new MemoryCacheEntryOptions
            {
                SlidingExpiration = ttl
            });
        }

        if (anyFailures)
        {
            logger.LogWarning(
                "Guide refresh completed with failures (isRefresh={IsRefresh}); stale entries retained.",
                isRefresh);
        }
    }

    private static string CacheKey(string stem) => CacheKeyPrefix + stem;
}
