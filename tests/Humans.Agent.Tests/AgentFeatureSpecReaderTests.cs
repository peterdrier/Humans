using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Agent.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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
    public async Task Every_spec_stem_in_the_repository_is_unique()
    {
        var paths = RepositoryMarkdownPaths();
        var reader = new AgentFeatureSpecReader(
            new TreeSource(paths),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AgentFeatureSpecReader>.Instance);

        var stems = await reader.KnownStemsAsync(TestContext.Current.CancellationToken);

        stems.Should().OnlyHaveUniqueItems();
        // A collapsed index means the walk found nothing and the assertion above is vacuous.
        stems.Should().NotBeEmpty();
    }

    /// <summary>
    /// The section-owned docs share a folder with the specs, so an exclusion rule that drifts
    /// would start serving invariants docs as feature specs.
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
        // …while the specs beside them are served.
        stems.Should().Contain("shift-management");
        stems.Should().Contain("gdpr-export");
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

    /// <summary>Serves a fixed markdown tree; no fetch path is exercised here.</summary>
    private sealed class TreeSource(IReadOnlyList<string> paths) : IGuideContentSource
    {
        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListMarkdownPathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(paths);
    }
}
