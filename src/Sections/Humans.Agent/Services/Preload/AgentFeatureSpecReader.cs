using Humans.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Octokit;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Reads a <c>docs/features/{stem}.md</c> file from the Humans repo on GitHub at runtime
/// via the shared <see cref="IGuideContentSource"/>. Held in memory with no expiration
/// (loaded once at startup or first call, refreshed only on restart).
/// Returns <c>null</c> on miss (invalid stem, GitHub 404, or transient failure).
/// </summary>
internal sealed class AgentFeatureSpecReader(
    IGuideContentSource source,
    IMemoryCache cache,
    ILogger<AgentFeatureSpecReader> logger)
{
    internal const string FolderPath = "docs/features";
    private const string CacheKeyPrefix = "agent:feature:";
    private const string StemsCacheKey = "agent:feature:stems";

    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };

    /// <summary>
    /// Lists the spec stems available under <c>docs/features</c> so a miss can tell the agent
    /// what it may ask for instead of dead-ending on "not found" (nobodies-collective/Humans#949).
    /// An unreachable folder yields an empty list, never an exception — the caller then falls back
    /// to the bare message rather than advertising an empty key set.
    /// </summary>
    public async Task<IReadOnlyList<string>> KnownStemsAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<IReadOnlyList<string>>(StemsCacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var stems = await source.ListMarkdownStemsAsync(FolderPath, cancellationToken);
            cache.Set(StemsCacheKey, stems, HoldForever);
            return stems;
        }
        catch (Exception ex)
        {
            // Log per memory/code/always-log-problems.md — a folder listing that keeps failing is
            // why the agent's miss messages stop naming any valid stems.
            logger.LogWarning(ex, "Failed to list agent feature specs in {Folder}; returning empty list", FolderPath);
            return [];
        }
    }

    public async Task<string?> ReadAsync(string stem, CancellationToken cancellationToken)
    {
        // Hard input validation — feature stems are filename-safe identifiers only.
        // Prevents arbitrary-path traversal via a crafted stem (e.g. "../secrets").
        if (string.IsNullOrWhiteSpace(stem) ||
            stem.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
            return null;

        var cacheKey = CacheKeyPrefix + stem;
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var body = await source.GetMarkdownAsync(FolderPath, stem, cancellationToken);
            cache.Set(cacheKey, body, HoldForever);
            return body;
        }
        catch (NotFoundException)
        {
            // 404 from GitHub — file is absent from the configured repo/branch. Log per
            // memory/code/always-log-problems.md so a missing feature spec is visible in
            // the prod log viewer rather than silently returning an empty preload entry.
            logger.LogWarning("Agent feature spec {Stem} not found on GitHub (docs/features)", stem);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to fetch agent feature spec {Stem} from GitHub; returning null", stem);
            return null;
        }
    }
}
