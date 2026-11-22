using Octokit;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Base.Configuration;

namespace Humans.Guide.Services;

internal sealed class GuideContentService(
    IGuideContentSource source,
    IGuideRenderer renderer,
    GuideSegmenter segmenter,
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

        try
        {
            return renderer.Render(GuideFilter.Apply(document, roleContext));
        }
        catch (RegexMatchTimeoutException ex)
        {
            // Outside PopulateAsync's per-file catch, a regex timeout would surface as a raw 500. Serve the
            // unavailable page and keep the cached segments: the next reader's filter may render fine.
            logger.LogError(ex,
                "Rendering guide file {FileStem} timed out; serving the unavailable page.",
                canonical);

            throw new GuideContentUnavailableException(
                $"Guide content '{canonical}' could not be rendered.", ex);
        }
    }

    public Task<IReadOnlyList<string>> RefreshAllAsync(CancellationToken cancellationToken = default) =>
        PopulateAsync(requestedStem: null, cancellationToken);

    private async Task<GuideDocument> GetDocumentAsync(string canonical, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey(canonical), out GuideDocument? cached) && cached is not null)
        {
            return cached;
        }

        await PopulateAsync(canonical, cancellationToken);

        if (cache.TryGetValue(CacheKey(canonical), out GuideDocument? afterPopulate) && afterPopulate is not null)
        {
            return afterPopulate;
        }

        throw new GuideContentUnavailableException(
            $"Guide content '{canonical}' is not currently available.");
    }

    private async Task<IReadOnlyList<string>> PopulateAsync(string? requestedStem, CancellationToken cancellationToken)
    {
        using var gate = await _refreshLock.AcquireAsync(logger, cancellationToken);

        if (requestedStem is not null &&
            cache.TryGetValue<GuideDocument>(CacheKey(requestedStem), out var loaded) && loaded is not null)
            return [];

        var ttl = TimeSpan.FromHours(Math.Max(1, settings.Value.CacheTtlHours));
        var failed = new List<string>();
        var newEntries = new Dictionary<string, GuideDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var stem in GuideFiles.All)
        {
            try
            {
                var markdown = await source.GetMarkdownAsync(stem, cancellationToken);
                newEntries[stem] = segmenter.Segment(markdown);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed.Add(stem);
                var expected = ex is ApiException or HttpRequestException or IOException or TimeoutException or OperationCanceledException;
                logger.Log(expected ? LogLevel.Warning : LogLevel.Error, expected ? null : ex,
                    "Failed to fetch or segment guide file {FileStem}; content is unavailable: {Reason}",
                    stem, ex.Message);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var stem in failed)
            cache.Remove(CacheKey(stem));

        foreach (var (stem, document) in newEntries)
        {
            cache.Set(CacheKey(stem), document, new MemoryCacheEntryOptions
            {
                SlidingExpiration = ttl
            });
        }

        if (failed.Count > 0)
        {
            logger.LogWarning(
                "Guide refresh completed with failures (isRefresh={IsRefresh}); failed pages are unavailable.",
                requestedStem is null);
        }

        return failed;
    }

    private static string CacheKey(string stem) => CacheKeyPrefix + stem;
}
