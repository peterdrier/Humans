using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Agent.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Humans.Agent.Contracts;

namespace Humans.Agent.Tests;

/// <summary>
/// The reader derives its servable set from the repository structure, so the guard that
/// matters is a structural one: the real tree must keep producing an unambiguous stem set.
/// Resolution and exclusion behaviour is covered against a stub tree in
/// <see cref="AgentToolDispatcherTests"/>.
/// </summary>
public class AgentFeatureSpecReaderTests
{
    /// <summary>
    /// <c>fetch_feature_spec</c> takes a bare stem, so two specs sharing a filename across
    /// two sections would leave one permanently unreachable — a silent loss with a green
    /// build. Fail here instead, at the moment the second one is committed.
    /// </summary>
    [HumansFact]
    public void Every_spec_stem_in_the_repository_is_unique()
    {
        // Asserted on the paths that feed the index, not on the index: its keys are unique by
        // construction, the colliding spec having already been dropped by TryAdd.
        var specPaths = RepositoryMarkdownPaths().Where(AgentFeatureSpecReader.IsSpecPath).ToList();

        // An empty set means the walk found nothing and the assertion below is vacuous.
        specPaths.Should().NotBeEmpty();

        // The index keys case-insensitively, so stems differing only in case collide too.
        specPaths
            .Select(p => p[(p.LastIndexOf('/') + 1)..^".md".Length].ToLowerInvariant())
            .Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Specs are exactly the contents of <c>Docs/features/</c>; the section-owned docs sit one
    /// level up in <c>Docs/</c>. A rule that drifted back to matching all of <c>Docs/</c> would
    /// start serving invariants docs as feature specs.
    /// </summary>
    [HumansFact]
    public async Task Section_owned_docs_are_not_served_as_feature_specs()
    {
        var reader = new AgentFeatureSpecReader(
            new TreeSource(RepositoryMarkdownPaths()),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AgentFeatureSpecReader>.Instance);

        var stems = await reader.KnownStemsAsync(TestContext.Current.CancellationToken);

        stems.Should().NotContain(["authorization", "data-access", "health"]);
        // The invariants doc is named for its section; Shifts.md is not a feature spec.
        stems.Should().NotContain("Shifts");
        stems.Should().NotContain(s => s.StartsWith("2026-", StringComparison.Ordinal),
            "dated design records are history, not specs of current behaviour");
        // A second invariants doc that happens not to be named for its section — the old rule
        // served it by accident, the folder boundary does not.
        stems.Should().NotContain("Holded-connector");
        // …while the specs one folder down are served, from a section project and from global.
        stems.Should().Contain("shift-management");
        stems.Should().Contain("gdpr-export");
    }

    /// <summary>
    /// The index is held with <c>NeverRemove</c> and <c>ReadAsync</c> treats an absent stem as
    /// "does not exist", so caching a tree 404 or a truncated tree would retire real specs for
    /// the rest of the process lifetime. An incomplete listing must be rebuilt every call.
    /// </summary>
    [HumansFact]
    public async Task An_incomplete_repository_listing_is_not_cached()
    {
        var source = new TreeSource(RepositoryMarkdownPaths(), isComplete: false);
        var reader = new AgentFeatureSpecReader(
            source,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AgentFeatureSpecReader>.Instance);

        await reader.KnownStemsAsync(TestContext.Current.CancellationToken);
        var stems = await reader.KnownStemsAsync(TestContext.Current.CancellationToken);

        source.ListCount.Should().Be(2);
        // Still served from the partial listing rather than withheld.
        stems.Should().Contain("shift-management");
    }

    /// <summary>The complement: a complete listing is indexed once and reused.</summary>
    [HumansFact]
    public async Task A_complete_repository_listing_is_cached()
    {
        var source = new TreeSource(RepositoryMarkdownPaths());
        var reader = new AgentFeatureSpecReader(
            source,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AgentFeatureSpecReader>.Instance);

        await reader.KnownStemsAsync(TestContext.Current.CancellationToken);
        await reader.KnownStemsAsync(TestContext.Current.CancellationToken);

        source.ListCount.Should().Be(1);
    }

    /// <summary>Every markdown file tracked in the working tree, as repo-root-relative paths.</summary>
    private static IReadOnlyList<string> RepositoryMarkdownPaths()
    {
        var root = LocateRepoRoot();
        return
        [
            .. Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
        ];
    }

    /// <summary>
    /// Walks up from the test binaries to the repo root. Inlined for the same reason
    /// <see cref="AgentSectionDocReaderTests"/> inlines it: this project needs the one member,
    /// not the harness around it (design §15 step 8).
    /// </summary>
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repository root (no Humans.slnx above " + AppContext.BaseDirectory + ").");
    }

    /// <summary>
    /// Serves a fixed markdown tree, counting listings so a test can tell a cached index from a
    /// rebuilt one. <paramref name="isComplete"/> mimics a tree 404 or a GitHub-truncated tree.
    /// </summary>
    private sealed class TreeSource(IReadOnlyList<string> paths, bool isComplete = true) : IGuideContentSource
    {
        public int ListCount { get; private set; }

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(CancellationToken cancellationToken = default)
        {
            ListCount++;
            return Task.FromResult((paths, isComplete));
        }
    }
}
