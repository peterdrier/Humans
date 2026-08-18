using Humans.Application.Interfaces;
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
    internal const string FolderPath = "docs/sections";
    private const string CacheKeyPrefix = "agent:section:";

    /// <summary>
    /// Where a section at G5 keeps its invariants doc (nobodies-collective/Humans#866
    /// design §7a): inside its own project rather than in <c>docs/sections</c>. Probed as
    /// a fallback so the tool keeps working across the migration without a per-section
    /// path map — the same convention for all ~35, whichever side of the move they are on.
    /// </summary>
    private static string SectionProjectFolder(string key) => $"src/Sections/Humans.{key}/Docs";

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
            var body = await FetchAsync(canonicalKey, cancellationToken);
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
                "Section guide {Section} not found on GitHub in {Folders}",
                canonicalKey, $"{FolderPath}, {SectionProjectFolder(canonicalKey)}");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to fetch agent section guide {Section} from GitHub; returning null", canonicalKey);
            return null;
        }
    }

    private async Task<string> FetchAsync(string canonicalKey, CancellationToken cancellationToken)
    {
        try
        {
            return await source.GetMarkdownAsync(FolderPath, canonicalKey, cancellationToken);
        }
        catch (NotFoundException)
        {
            return await source.GetMarkdownAsync(
                SectionProjectFolder(canonicalKey), canonicalKey, cancellationToken);
        }
    }

    public IReadOnlySet<string> KnownSections => AgentSectionKeys.All;
}
