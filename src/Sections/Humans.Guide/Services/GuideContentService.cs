using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Humans.Application.Interfaces;
using Humans.Application.Threading;
using Humans.Infrastructure.Configuration;

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

    public async Task<string> GetRenderedAsync(string fileStem, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);

        if (!GuideFiles.TryCanonical(fileStem, out var canonical))
        {
            throw new FileNotFoundException($"Guide file '{fileStem}' is not in the known set.");
        }

        if (cache.TryGetValue(CacheKey(canonical), out string? cached) && cached is not null)
        {
            return cached;
        }

        await PopulateAsync(isRefresh: false, cancellationToken);

        if (cache.TryGetValue(CacheKey(canonical), out string? afterPopulate) && afterPopulate is not null)
        {
            return afterPopulate;
        }

        throw new GuideContentUnavailableException(
            $"Guide content '{canonical}' is not currently available.");
    }

    public Task RefreshAllAsync(CancellationToken cancellationToken = default) =>
        PopulateAsync(isRefresh: true, cancellationToken);

    private async Task PopulateAsync(bool isRefresh, CancellationToken cancellationToken)
    {
        using var gate = await _refreshLock.AcquireAsync(logger, cancellationToken);

        var hasStale = GuideFiles.All.Any(s => cache.TryGetValue(CacheKey(s), out string? _));

        var ttl = TimeSpan.FromHours(Math.Max(1, settings.Value.CacheTtlHours));
        var anyFailures = false;
        var newEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stem in GuideFiles.All)
        {
            try
            {
                var markdown = await source.GetMarkdownAsync(stem, cancellationToken);
                var html = renderer.Render(markdown, stem);
                newEntries[stem] = html;
            }
            catch (Exception ex)
            {
                anyFailures = true;
                logger.LogWarning(ex,
                    "Failed to fetch or render guide file {FileStem}; {Outcome}",
                    stem,
                    hasStale ? "keeping stale cached copy" : "no stale copy available");
            }
        }

        if (!hasStale && newEntries.Count == 0)
        {
            throw new GuideContentUnavailableException(
                "Guide content is unavailable and the cache is cold.");
        }

        foreach (var (stem, html) in newEntries)
        {
            cache.Set(CacheKey(stem), html, new MemoryCacheEntryOptions
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
