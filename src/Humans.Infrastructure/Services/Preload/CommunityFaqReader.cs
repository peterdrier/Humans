using Humans.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>
/// Reads the community-sourced FAQ corpus (Discord-extracted markdown vendored into
/// <c>docs/community-kb/</c>) from the Humans repo on GitHub via the shared
/// <see cref="IGuideContentSource"/>. The file set is discovered dynamically (no hardcoded
/// list) so new topics appear without a code change. Held in RAM with no expiration —
/// content is GitHub-backed and only changes at release, which restarts the process.
/// This corpus is unofficial; callers must surface its provenance (see
/// <see cref="WrapWithProvenance"/>).
/// </summary>
public sealed class CommunityFaqReader(
    IGuideContentSource source,
    IMemoryCache cache,
    ILogger<CommunityFaqReader> logger)
{
    internal const string FolderPath = "docs/community-kb";
    private const string IndexCacheKey = "agent:community-kb:index";
    private const string DocCacheKeyPrefix = "agent:community-kb:doc:";

    // No expiration + NeverRemove: loaded once at startup, held for the process lifetime,
    // not evictable under memory pressure. Restart is the refresh.
    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };

    public sealed record IndexEntry(string Topic, string Title, string? LastUpdated, string Summary);

    public async Task<IReadOnlyList<IndexEntry>> ListTopicsAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<IReadOnlyList<IndexEntry>>(IndexCacheKey, out var cached) && cached is not null)
            return cached;

        IReadOnlyList<string> stems;
        try
        {
            stems = await source.ListMarkdownStemsAsync(FolderPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list community KB folder {Folder}; returning empty index", FolderPath);
            return [];
        }

        var entries = new List<IndexEntry>();
        foreach (var stem in stems.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var body = await ReadRawAsync(stem, cancellationToken);
            if (body is null) continue;
            entries.Add(ParseIndexEntry(stem, body));
        }

        IReadOnlyList<IndexEntry> result = entries;
        cache.Set(IndexCacheKey, result, HoldForever);
        return result;
    }

    public async Task<string?> ReadAsync(string topic, CancellationToken cancellationToken)
    {
        if (!IsSafeTopic(topic)) return null;

        // Only serve topics that actually exist in the discovered set — defends against
        // a crafted-but-safe-looking key and keeps the cache key space bounded.
        var known = await ListTopicsAsync(cancellationToken);
        if (!known.Any(e => string.Equals(e.Topic, topic, StringComparison.OrdinalIgnoreCase)))
            return null;

        return await ReadRawAsync(topic, cancellationToken);
    }

    private async Task<string?> ReadRawAsync(string stem, CancellationToken cancellationToken)
    {
        var cacheKey = DocCacheKeyPrefix + stem;
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
            logger.LogWarning("Community KB file {Stem} not found on GitHub ({Folder})", stem, FolderPath);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch community KB file {Stem} from GitHub; returning null", stem);
            return null;
        }
    }

    private static bool IsSafeTopic(string topic) =>
        !string.IsNullOrWhiteSpace(topic) &&
        topic.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>
    /// Prepends a provenance header so the model always sees that this content is
    /// community-sourced and unofficial, even late in a long turn.
    /// </summary>
    public static string WrapWithProvenance(string body)
    {
        var lastUpdated = ExtractLastUpdated(body);
        var header = lastUpdated is null
            ? "SOURCE: community Discord FAQ · NOT official · may be outdated"
            : $"SOURCE: community Discord FAQ · NOT official · may be outdated · last updated {lastUpdated}";
        return header +
               "\nWhen you use anything below, tell the user it comes from community discussion and may not be official.\n\n" +
               body;
    }

    internal static IndexEntry ParseIndexEntry(string topic, string body)
    {
        var lines = body.Split('\n');
        var title = topic;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
                break;
            }
        }

        var summary = ExtractOverview(lines) ?? title;
        return new IndexEntry(topic, title, ExtractLastUpdated(body), summary);
    }

    private static string? ExtractLastUpdated(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("Last updated", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                return colon >= 0 && colon < line.Length - 1
                    ? line[(colon + 1)..].Trim()
                    : line;
            }
        }
        return null;
    }

    private static string? ExtractOverview(string[] lines)
    {
        var inOverview = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!inOverview)
            {
                if (line.Trim().Equals("## Overview", StringComparison.OrdinalIgnoreCase))
                    inOverview = true;
                continue;
            }
            if (line.StartsWith("##", StringComparison.Ordinal)) return null; // next heading, no body
            if (line.Trim().Length == 0) continue;
            var text = line.Trim();
            return text.Length > 200 ? text[..200] : text;
        }
        return null;
    }
}
