using Humans.Base.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Octokit;
using Humans.Agent.Contracts;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Held in memory with no expiration: restart refreshes. Null on any miss, so the caller can degrade gracefully.
/// </summary>
internal sealed class AgentSectionDocReader(
    IGuideContentSource source,
    IMemoryCache cache,
    ILogger<AgentSectionDocReader> logger)
{
    private const string CacheKeyPrefix = "agent:section:";

    /// <summary>
    /// Internal because <c>SectionAnnotations</c> and the docs health check use the same folder: one copy of the path.
    /// </summary>
    internal static string SectionProjectFolder(string key) => $"src/Sections/Humans.{key}/Docs";

    // GitHub-backed content that only changes at release: held for the process lifetime.
    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!AgentSectionKeys.TryResolve(key, out var canonicalKey)) return null;

        var cacheKey = CacheKeyPrefix + canonicalKey;
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var body = await source.GetMarkdownAsync(
                SectionProjectFolder(canonicalKey), canonicalKey, cancellationToken);
            cache.Set(cacheKey, body, HoldForever);
            return body;
        }
        catch (NotFoundException)
        {
            // Whitelisted but missing: a miss, so the tool degrades cleanly. Logged at Warning, which the prod log
            // viewer shows.
            logger.LogWarning(
                "Section guide {Section} not found on GitHub in {Folder}",
                canonicalKey, SectionProjectFolder(canonicalKey));
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Failed to fetch agent section guide {Section} from GitHub; returning null", canonicalKey);
            return null;
        }
    }

    public IReadOnlySet<string> KnownSections => AgentSectionKeys.All;
}
