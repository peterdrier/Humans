using System.Text.RegularExpressions;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Octokit;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Stems resolve through an index of the repository tree, since specs live in each section's <c>Docs/features/</c>
/// plus <c>docs/features/global/</c>. Held in memory with no expiration: restart refreshes. Null on any miss.
/// </summary>
internal sealed partial class AgentFeatureSpecReader(
    IGuideContentSource source,
    IMemoryCache cache,
    ILogger<AgentFeatureSpecReader> logger)
{
    internal const string GlobalFolderPath = "docs/features/global";
    private const string CacheKeyPrefix = "agent:feature:";
    private const string IndexCacheKey = "agent:feature:index";

    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };

    [GeneratedRegex(@"^src/Sections/Humans\.[^/]+/Docs/features/[^/]+\.md$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SectionSpecPath();

    /// <summary>
    /// The folder is the whole rule: invariants docs and their companions live directly in <c>Docs/</c>. Internal so
    /// the stem-collision test can apply the real rule to the real tree.
    /// </summary>
    internal static bool IsSpecPath(string path) =>
        SectionSpecPath().IsMatch(path)
        || (path.StartsWith(GlobalFolderPath + "/", StringComparison.OrdinalIgnoreCase)
            && path.LastIndexOf('/') == GlobalFolderPath.Length);

    /// <summary>
    /// An unreachable tree yields an empty index, never an exception, so the caller falls back to the bare miss
    /// message rather than advertising an empty key set.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> IndexAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<IReadOnlyDictionary<string, string>>(IndexCacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var (paths, isComplete) = await source.ListMarkdownPathsAsync(cancellationToken);
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths.Where(IsSpecPath))
            {
                var slash = path.LastIndexOf('/');
                var stem = path[(slash + 1)..^".md".Length];
                // The tool takes a bare stem, so two colliding stems can only serve one: say which.
                if (!index.TryAdd(stem, path[..slash]))
                {
                    logger.LogWarning(
                        "Feature spec stem {Stem} is ambiguous: {Path} is shadowed by {Served}",
                        stem, path, index[stem] + "/" + stem + ".md");
                }
            }

            // Held forever, so only a listing known to be the whole corpus may be stored:
            // ReadAsync treats an absent stem as "does not exist", and a tree 404 or a
            // GitHub-truncated tree would otherwise retire real specs until the process
            // restarts. An incomplete index still serves this call, and is rebuilt on the next.
            if (isComplete)
            {
                cache.Set(IndexCacheKey, (IReadOnlyDictionary<string, string>)index, HoldForever);
            }
            else
            {
                logger.LogWarning(
                    "Feature spec index built from an incomplete repository listing ({Count} specs); not caching",
                    index.Count);
            }

            return index;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // An index that keeps failing is why the agent's miss messages stop naming any stems.
            logger.LogWarning(ex, "Failed to index agent feature specs; returning an empty index");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Lets a miss tell the agent what it may ask for.</summary>
    public async Task<IReadOnlyList<string>> KnownStemsAsync(CancellationToken cancellationToken) =>
        [.. (await IndexAsync(cancellationToken)).Keys];

    public async Task<string?> ReadAsync(string stem, CancellationToken cancellationToken)
    {
        // Filename-safe stems only: blocks path traversal via a crafted stem.
        if (string.IsNullOrWhiteSpace(stem) ||
            stem.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
            return null;

        var cacheKey = CacheKeyPrefix + stem;
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        var index = await IndexAsync(cancellationToken);
        if (!index.TryGetValue(stem, out var folderPath))
        {
            // Logged so a spec the model keeps asking for is visible in the prod log viewer.
            logger.LogWarning("Agent feature spec {Stem} is not in the repository's spec index", stem);
            return null;
        }

        try
        {
            var body = await source.GetMarkdownAsync(folderPath, stem, cancellationToken);
            cache.Set(cacheKey, body, HoldForever);
            return body;
        }
        catch (NotFoundException)
        {
            // Indexed but absent — the tree and the contents API disagree, which happens while a
            // branch moves under a long-lived process.
            logger.LogWarning(
                "Agent feature spec {Stem} not found on GitHub in {Folder}", stem, folderPath);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Failed to fetch agent feature spec {Stem} from GitHub; returning null", stem);
            return null;
        }
    }
}
