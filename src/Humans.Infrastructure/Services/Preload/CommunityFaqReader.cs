using Humans.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>
/// Reads the community-sourced FAQ corpus (Discord-extracted markdown under
/// <c>docs/community-kb/</c>) from the dedicated knowledge-base repo on GitHub via an
/// <see cref="IGuideContentSource"/> bound to that repo. The file set is discovered
/// dynamically (no hardcoded list) so new topics appear without a code change. Held in RAM
/// with no expiration; refreshed by the admin "Reload KB" action (<see cref="ReloadAsync"/>)
/// or an app restart. This corpus is unofficial; callers must surface its provenance (see
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

    public sealed record IndexEntry(string Topic, string Title, string? LastUpdated, string Summary, string Keywords);

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

        // Resolve the caller's casing to the canonical filename stem from the discovered set.
        // LLMs routinely lowercase the topic key, but GitHub paths and the per-doc cache key
        // are case-sensitive, so we must fetch with the canonical stem, not the caller's
        // (mirrors AgentSectionDocReader). This also restricts reads to known topics and
        // bounds the cache key space.
        var known = await ListTopicsAsync(cancellationToken);
        var canonical = known.FirstOrDefault(e => string.Equals(e.Topic, topic, StringComparison.OrdinalIgnoreCase))?.Topic;
        if (canonical is null) return null;

        return await ReadRawAsync(canonical, cancellationToken);
    }

    /// <summary>
    /// Force-refreshes the corpus from GitHub and swaps it into the cache: re-lists the folder,
    /// re-fetches every file (bypassing the cache), and overwrites the per-file + index entries.
    /// On a listing failure the existing cache is left intact (no blow-away).
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> stems;
        try
        {
            stems = await source.ListMarkdownStemsAsync(FolderPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Community KB reload: listing {Folder} failed; keeping existing cache", FolderPath);
            return;
        }

        var entries = new List<IndexEntry>();
        foreach (var stem in stems.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            string body;
            try
            {
                body = await source.GetMarkdownAsync(FolderPath, stem, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Community KB reload: fetch failed for {Stem}; skipping", stem);
                continue;
            }
            cache.Set(DocCacheKeyPrefix + stem, body, HoldForever);
            entries.Add(ParseIndexEntry(stem, body));
        }

        cache.Set(IndexCacheKey, (IReadOnlyList<IndexEntry>)entries, HoldForever);
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
        return new IndexEntry(topic, title, ExtractLastUpdated(body), summary, ExtractKeywords(lines));
    }

    /// <summary>
    /// Harvests routing keywords from a topic file so the preloaded index can show what each
    /// topic actually covers — the one-line Overview alone hides terms like "urinals", "EE", or
    /// "VIPee" that only appear in Key facts / FAQ, leaving the router unable to tell the topic
    /// is relevant. We take the bold lead-in of every bullet (<c>- **…**</c>) and every standalone
    /// bold line (the FAQ questions), de-duplicate case-insensitively, and cap the count so the
    /// index stays bounded as the corpus grows. The Overview paragraph itself is unchanged
    /// (<see cref="ExtractOverview"/>); keywords are an additive routing aid.
    /// </summary>
    internal static string ExtractKeywords(string[] lines)
    {
        const int maxKeywords = 100;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').Trim();
            // Bullet lead-ins (Key facts) and standalone bold lines (FAQ questions) are the
            // signal; prose paragraphs (Overview) and headings are skipped.
            if (!line.StartsWith("- ", StringComparison.Ordinal) && !line.StartsWith("**", StringComparison.Ordinal))
                continue;

            var bold = FirstBoldSpan(line);
            if (bold is null) continue;
            var keyword = bold.Trim().TrimEnd('.', ':', '?', '!', ',').Trim();
            if (keyword.Length == 0) continue;
            if (seen.Add(keyword))
            {
                ordered.Add(keyword);
                if (ordered.Count >= maxKeywords) break;
            }
        }
        return string.Join(" · ", ordered);
    }

    /// <summary>Returns the text inside the first <c>**bold**</c> span on a line, or null if there is none.</summary>
    private static string? FirstBoldSpan(string line)
    {
        var open = line.IndexOf("**", StringComparison.Ordinal);
        if (open < 0) return null;
        var close = line.IndexOf("**", open + 2, StringComparison.Ordinal);
        if (close < 0) return null;
        var inner = line[(open + 2)..close];
        return inner.Length == 0 ? null : inner;
    }

    private static string? ExtractLastUpdated(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("Last updated", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                var value = colon >= 0 && colon < line.Length - 1 ? line[(colon + 1)..].Trim() : line;
                // Keep only the date itself; drop trailing pipeline prose like
                // "· windows merged through ..." so the provenance header stays terse.
                var sep = value.IndexOf('·');
                return sep > 0 ? value[..sep].Trim() : value;
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
            if (text.Length <= 200) return text;
            // Trim back to the last word boundary so the routing summary doesn't cut mid-word.
            var cut = text[..200];
            var lastSpace = cut.LastIndexOf(' ');
            return (lastSpace > 0 ? cut[..lastSpace] : cut) + "…";
        }
        return null;
    }
}
