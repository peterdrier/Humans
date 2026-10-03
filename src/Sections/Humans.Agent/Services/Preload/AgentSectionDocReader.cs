using Humans.Base.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Octokit;
using Humans.Agent.Contracts;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Reads a whitelisted section's invariants doc from the Humans repo on GitHub at runtime
/// via the shared <see cref="IGuideContentSource"/>. Held in memory with no
/// expiration (loaded once at startup or first call, refreshed only on restart) so
/// per-tool-call round trips are avoided. Returns <c>null</c> on miss (unknown
/// key, GitHub 404, or transient fetch failure) so the caller can degrade gracefully.
/// </summary>
internal sealed class AgentSectionDocReader(
    IGuideContentSource source,
    IMemoryCache cache,
    ILogger<AgentSectionDocReader> logger)
{
    private const string CacheKeyPrefix = "agent:section:";

    /// <summary>
    /// Where a section keeps its invariants doc: inside its own project
    /// (nobodies-collective/Humans#866 design §7a). Internal because <c>SectionAnnotations</c>
    /// reports the same folder on /Debug/Sections and the docs health check probes it; a second
    /// copy of the path would be a second thing to get wrong.
    /// </summary>
    internal static string SectionProjectFolder(string key) => $"src/Sections/Humans.{key}/Docs";

    // No expiration + NeverRemove: GitHub-backed content that only changes at release.
    // Loaded once (startup warm-up or first call) and held for the process lifetime.
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
            // Whitelisted key but no file in the repo — treat as miss so the tool degrades
            // cleanly rather than crashing the dispatcher. Log per
            // memory/code/always-log-problems.md so a missing section guide is visible in
            // the prod log viewer (which only renders Warning+) instead of disappearing.
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
