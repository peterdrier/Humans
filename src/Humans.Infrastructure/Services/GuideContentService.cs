using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

public sealed class GuideContentService(
    IGuideContentSource source,
    IGuideRenderer renderer,
    IMemoryCache cache,
    IOptions<GuideSettings> settings,
    ILogger<GuideContentService> logger) : IGuideContentService
{
    private const string CacheKeyPrefix = "guide:";

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<string> GetRenderedAsync(string fileStem, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);

        var canonical = GuideFiles.All.FirstOrDefault(s => s.Equals(fileStem, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Guide file '{fileStem}' is not in the known set.");

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
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var hasStale = GuideFiles.All.Any(s => cache.TryGetValue(CacheKey(s), out string? _));

            var settings1 = settings.Value;
            var ttl = TimeSpan.FromHours(Math.Max(1, settings1.CacheTtlHours));
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
        finally
        {
            _refreshLock.Release();
        }
    }

    private static string CacheKey(string stem) => CacheKeyPrefix + stem;
}
