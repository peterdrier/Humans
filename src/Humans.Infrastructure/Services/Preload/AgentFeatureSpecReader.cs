using Humans.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>
/// Reads a <c>docs/features/{stem}.md</c> file from the Humans repo on GitHub at runtime
/// via the shared <see cref="IGuideContentSource"/>. Held in memory with no expiration
/// (loaded once at startup or first call, refreshed only on restart).
/// Returns <c>null</c> on miss (invalid stem, GitHub 404, or transient failure).
/// </summary>
public sealed class AgentFeatureSpecReader(
    IGuideContentSource source,
    IMemoryCache cache,
    ILogger<AgentFeatureSpecReader> logger)
{
    internal const string FolderPath = "docs/features";
    private const string CacheKeyPrefix = "agent:feature:";

    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };

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
