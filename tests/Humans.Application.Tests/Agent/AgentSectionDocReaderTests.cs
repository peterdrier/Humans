using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Humans.Application.Tests.Agent;

/// <summary>
/// Guards the case-insensitive whitelist + canonical-cased path resolution
/// (nobodies-collective/Humans#789). LLMs routinely lowercase the section key
/// (e.g. "shifts"); the reader must canonicalize it to the on-GitHub filename
/// ("Shifts.md") because GitHub paths are case-sensitive.
/// </summary>
public class AgentSectionDocReaderTests
{
    [HumansTheory]
    [InlineData("Shifts")]
    [InlineData("shifts")]
    [InlineData("SHIFTS")]
    public async Task ReadAsync_resolves_any_casing_to_the_canonical_section_file(string key)
    {
        var source = new FakeSource();
        var reader = MakeReader(source);

        var content = await reader.ReadAsync(key, TestContext.Current.CancellationToken);

        content.Should().NotBeNullOrEmpty();
        source.LastFolder.Should().Be(AgentSectionDocReader.FolderPath);
        source.LastStem.Should().Be("Shifts", "the reader must canonicalize the key, not pass caller casing");
    }

    /// <summary>
    /// A whitelisted key with no matching file is unreachable at runtime and fails silently —
    /// <see cref="AgentSectionDocReader"/> swallows the GitHub 404 and returns null, and the
    /// docs health check only probes Shifts, so a typo would never surface. The repo is the
    /// source these docs are served from, so the local folder is the authority.
    /// </summary>
    [HumansFact]
    public void Every_whitelisted_section_has_a_matching_doc_file()
    {
        var folder = Path.Combine(RatchetTestRunner.LocateRepoRoot(), "docs", "sections");
        var stems = Directory.GetFiles(folder, "*.md").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);

        // Ordinal, not OrdinalIgnoreCase: GitHub paths are case-sensitive, and the reader
        // fetches "{canonicalKey}.md" verbatim.
        MakeReader(new FakeSource()).KnownSections.Should().OnlyContain(
            key => stems.Contains(key),
            "every whitelisted key is fetched as docs/sections/{key}.md with exact casing");
    }

    /// <summary>
    /// The keys the model reads out of its own preload corpus (help-widget glossary keys,
    /// access-matrix display names) and out of user jargon are a different namespace from the
    /// whitelist. Every one of these was a real production lookup that dead-ended
    /// (nobodies-collective/Humans#949).
    /// </summary>
    [HumansTheory]
    [InlineData("Profile", "Profiles")]
    [InlineData("profile", "Profiles")]
    [InlineData("Barrios", "Camps")]
    [InlineData("CityPlanningOverview", "CityPlanning")]
    [InlineData("CityPlanningBarrioMap", "CityPlanning")]
    [InlineData("ContainerMap", "Containers")]
    [InlineData("OnboardingReview", "Onboarding")]
    [InlineData("Board", "Governance")]
    [InlineData("Admin", "Governance")]
    [InlineData("LegalAndConsent", "Consent")]
    public async Task ReadAsync_resolves_the_aliases_the_agent_reads_out_of_its_own_prompt(string key, string expectedStem)
    {
        var source = new FakeSource();
        var reader = MakeReader(source);

        var content = await reader.ReadAsync(key, TestContext.Current.CancellationToken);

        content.Should().NotBeNullOrEmpty();
        source.LastStem.Should().Be(expectedStem);
    }

    /// <summary>
    /// An alias pointing at a non-whitelisted key would resolve and then fetch nothing — the same
    /// silent dead end, one indirection further away.
    /// </summary>
    [HumansTheory]
    [InlineData("Profile")]
    [InlineData("Barrios")]
    [InlineData("CityPlanningOverview")]
    [InlineData("CityPlanningBarrioMap")]
    [InlineData("ContainerMap")]
    [InlineData("OnboardingReview")]
    [InlineData("Board")]
    [InlineData("Admin")]
    [InlineData("LegalAndConsent")]
    public void Every_alias_target_is_a_whitelisted_section(string alias)
    {
        Humans.Application.Constants.AgentSectionKeys.TryResolve(alias, out var target).Should().BeTrue();
        MakeReader(new FakeSource()).KnownSections.Should().Contain(target!);
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_for_non_whitelisted_key()
    {
        var reader = MakeReader(new FakeSource());

        var content = await reader.ReadAsync("NotASection", TestContext.Current.CancellationToken);

        content.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_when_github_returns_not_found()
    {
        var source = new FakeSource { FailWith = new NotFoundException("missing", System.Net.HttpStatusCode.NotFound) };
        var reader = MakeReader(source);

        var content = await reader.ReadAsync("Shifts", TestContext.Current.CancellationToken);

        content.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_on_transient_github_failure()
    {
        var source = new FakeSource { FailWith = new InvalidOperationException("network down") };
        var reader = MakeReader(source);

        var content = await reader.ReadAsync("Shifts", TestContext.Current.CancellationToken);

        content.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadAsync_caches_successful_fetch()
    {
        var source = new FakeSource();
        var reader = MakeReader(source);

        await reader.ReadAsync("Shifts", TestContext.Current.CancellationToken);
        await reader.ReadAsync("Shifts", TestContext.Current.CancellationToken);

        source.CallCount.Should().Be(1);
    }

    private static AgentSectionDocReader MakeReader(FakeSource source) =>
        new(
            source,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AgentSectionDocReader>.Instance);

    private sealed class FakeSource : IGuideContentSource
    {
        public int CallCount { get; private set; }
        public string? LastFolder { get; private set; }
        public string? LastStem { get; private set; }
        public Exception? FailWith { get; set; }

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Agent reader must use the folder-parameterized overload.");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastFolder = folderPath;
            LastStem = fileStem;
            if (FailWith is not null) throw FailWith;
            return Task.FromResult($"# {fileStem}\n\nBody.");
        }

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
