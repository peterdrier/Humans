using Humans.Base.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Octokit;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Reads the community-sourced FAQ corpus (Discord-extracted markdown under
/// <c>docs/community-kb/</c>) from the dedicated knowledge-base repo on GitHub via an
/// <see cref="IGuideContentSource"/> bound to that repo. The file set is discovered
/// dynamically (no hardcoded list) so new topics appear without a code change. Held in RAM
/// with no expiration; refreshed by the admin "Reload KB" action (<see cref="ReloadAsync"/>)
/// or an app restart. This corpus is unofficial; callers must surface its provenance (see
/// <see cref="WrapWithProvenance"/>).
/// </summary>
internal sealed class CommunityFaqReader(
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

    private readonly Lock _cacheGate = new();
    private long _cacheGeneration;

    internal sealed record IndexEntry(string Topic, string Title, string? LastUpdated, string Summary, string Keywords);

    public async Task<(IReadOnlyList<IndexEntry> Entries, bool IsComplete)> ListTopicsAsync(CancellationToken cancellationToken)
    {
        long generation;
        lock (_cacheGate)
        {
            if (cache.TryGetValue<IReadOnlyList<IndexEntry>>(IndexCacheKey, out var cached) && cached is not null)
                return (cached, true);
            generation = _cacheGeneration;
        }

        IReadOnlyList<string> stems;
        try
        {
            stems = await source.ListMarkdownStemsAsync(FolderPath, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to list community KB folder {Folder}; returning empty index", FolderPath);
            return ([], false);
        }

        var entries = new List<IndexEntry>();
        var isComplete = true;
        foreach (var stem in stems.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var body = await ReadRawAsync(stem, cancellationToken);
            if (body is null)
            {
                isComplete = false;
                continue;
            }
            entries.Add(ParseIndexEntry(stem, body));
        }

        IReadOnlyList<IndexEntry> result = entries;
        lock (_cacheGate)
        {
            if (isComplete && generation == _cacheGeneration)
                cache.Set(IndexCacheKey, result, HoldForever);
        }
        return (result, isComplete);
    }

    public async Task<string?> ReadAsync(string topic, CancellationToken cancellationToken)
    {
        if (!IsSafeTopic(topic)) return null;

        // Resolve the caller's casing to the canonical filename stem from the discovered set.
        // LLMs routinely lowercase the topic key, but GitHub paths and the per-doc cache key
        // are case-sensitive, so we must fetch with the canonical stem, not the caller's
        // (mirrors AgentSectionDocReader). This also restricts reads to known topics and
        // bounds the cache key space.
        var (known, _) = await ListTopicsAsync(cancellationToken);
        var canonical = known.FirstOrDefault(e => string.Equals(e.Topic, topic, StringComparison.OrdinalIgnoreCase))?.Topic;
        if (canonical is null) return null;

        return await ReadRawAsync(canonical, cancellationToken);
    }

    /// <summary>
    /// Force-refreshes the corpus from GitHub and swaps it into the cache: re-lists the folder,
    /// re-fetches every file (bypassing the cache), and overwrites the per-file + index entries.
    /// On any fetch/listing failure the existing snapshot is left intact. Returns whether
    /// a complete replacement was published.
    /// </summary>
    public async Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> stems;
        try
        {
            stems = await source.ListMarkdownStemsAsync(FolderPath, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Community KB reload: listing {Folder} failed; keeping existing cache", FolderPath);
            return false;
        }

        var entries = new List<IndexEntry>();
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stem in stems.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            string body;
            try
            {
                body = await source.GetMarkdownAsync(FolderPath, stem, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Community KB reload: fetch failed for {Stem}; keeping existing cache", stem);
                return false;
            }
            documents.Add(stem, body);
            entries.Add(ParseIndexEntry(stem, body));
        }

        // Stage every document before changing any cached body or index.
        lock (_cacheGate)
        {
            _cacheGeneration++;
            foreach (var (stem, body) in documents)
                cache.Set(DocCacheKeyPrefix + stem, body, HoldForever);
            cache.Set(IndexCacheKey, (IReadOnlyList<IndexEntry>)entries, HoldForever);
        }
        return true;
    }

    private async Task<string?> ReadRawAsync(string stem, CancellationToken cancellationToken)
    {
        var cacheKey = DocCacheKeyPrefix + stem;
        long generation;
        lock (_cacheGate)
        {
            if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
                return cached;
            generation = _cacheGeneration;
        }

        try
        {
            var body = await source.GetMarkdownAsync(FolderPath, stem, cancellationToken);
            lock (_cacheGate)
            {
                if (generation == _cacheGeneration)
                    cache.Set(cacheKey, body, HoldForever);
            }
            return body;
        }
        catch (NotFoundException)
        {
            logger.LogWarning("Community KB file {Stem} not found on GitHub ({Folder})", stem, FolderPath);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
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
    /// Reads the explicit <c>## Keywords</c> routing line a topic file declares, so the preloaded
    /// index can show what each topic covers — the one-line Overview alone hides terms like
    /// "urinals", "EE", or "VIPee" that the router needs to recognise a topic is relevant. The app
    /// deliberately does NOT derive keywords from the prose: real per-file keyword extraction
    /// (stopwords, proper nouns, EN/ES) is an offline, reviewable concern owned by the KB generator
    /// pipeline, not the app. Reads from the <c>## Keywords</c> heading until the next <c>##</c>,
    /// returning that text with newlines collapsed to spaces, or "" when the file declares no
    /// Keywords section, in which case the index shows the Overview summary alone.
    /// </summary>
    internal static string ExtractKeywords(string[] lines)
    {
        var inSection = false;
        var collected = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!inSection)
            {
                if (line.Trim().Equals("## Keywords", StringComparison.OrdinalIgnoreCase))
                    inSection = true;
                continue;
            }
            if (line.StartsWith("##", StringComparison.Ordinal)) break; // next heading ends the section
            var text = line.Trim();
            if (text.Length > 0) collected.Add(text);
        }
        return string.Join(' ', collected).Trim();
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
            var length = char.IsHighSurrogate(text[199]) && char.IsLowSurrogate(text[200]) ? 199 : 200;
            var cut = text[..length];
            var lastSpace = cut.LastIndexOf(' ');
            return (lastSpace > 0 ? cut[..lastSpace] : cut) + "…";
        }
        return null;
    }
}
