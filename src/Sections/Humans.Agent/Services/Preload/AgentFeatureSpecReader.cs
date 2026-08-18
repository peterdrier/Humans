using System.Text.RegularExpressions;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Octokit;

namespace Humans.Agent.Services.Preload;

/// <summary>
/// Reads one feature spec from the Humans repo on GitHub at runtime via the shared
/// <see cref="IGuideContentSource"/>. Specs live in the owning section's own
/// <c>src/Sections/Humans.&lt;Section&gt;/Docs/features/</c> folder, plus
/// <c>docs/features/global/</c> for the cross-section ones, so the stem the tool is given is
/// resolved through an index derived from the repository tree rather than a fixed folder.
/// Held in memory with no expiration (loaded once at startup or first call, refreshed only
/// on restart).
/// Returns <c>null</c> on miss (unknown stem, GitHub 404, or transient failure).
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

    /// <summary>A section project's spec folder: <c>src/Sections/Humans.{Section}/Docs/features/{file}.md</c>.</summary>
    [GeneratedRegex(@"^src/Sections/Humans\.[^/]+/Docs/features/[^/]+\.md$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SectionSpecPath();

    /// <summary>
    /// True for a repo path that is a feature spec: a file in a section's <c>Docs/features/</c>,
    /// or one directly under <c>docs/features/global/</c> for the cross-section specs. The
    /// folder is the whole rule — a section's invariants doc, its generated companions
    /// (<c>authorization.md</c>, <c>data-access.md</c>, <c>health.md</c>) and its dated design
    /// records stay directly in <c>Docs/</c> and are excluded by living outside the folder,
    /// with nothing to keep in sync as those file sets change.
    /// Internal so the stem-collision test can apply the real rule to the real tree; reading
    /// the built index back cannot detect a collision, having already dropped the loser.
    /// </summary>
    internal static bool IsSpecPath(string path) =>
        SectionSpecPath().IsMatch(path)
        || (path.StartsWith(GlobalFolderPath + "/", StringComparison.OrdinalIgnoreCase)
            && path.LastIndexOf('/') == GlobalFolderPath.Length);

    /// <summary>
    /// Stem → containing folder for every spec in the repo, derived from one recursive tree
    /// listing. An unreachable tree yields an empty index, never an exception — the caller then
    /// falls back to the bare miss message rather than advertising an empty key set.
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
                // Stems are unique across sections today; the tool takes a bare stem, so if two
                // ever collide it can only serve one — say which rather than let the loser
                // vanish silently (memory/code/always-log-problems.md).
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
        catch (Exception ex)
        {
            // Log per memory/code/always-log-problems.md — an index that keeps failing is why
            // the agent's miss messages stop naming any valid stems.
            logger.LogWarning(ex, "Failed to index agent feature specs; returning an empty index");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Lists the spec stems available so a miss can tell the agent what it may ask for instead
    /// of dead-ending on "not found" (nobodies-collective/Humans#949).
    /// </summary>
    public async Task<IReadOnlyList<string>> KnownStemsAsync(CancellationToken cancellationToken) =>
        [.. (await IndexAsync(cancellationToken)).Keys];

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

        var index = await IndexAsync(cancellationToken);
        if (!index.TryGetValue(stem, out var folderPath))
        {
            // Not in the repo tree. Log per memory/code/always-log-problems.md so a spec the
            // model keeps asking for is visible in the prod log viewer rather than silently
            // returning an empty preload entry.
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
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to fetch agent feature spec {Stem} from GitHub; returning null", stem);
            return null;
        }
    }
}
