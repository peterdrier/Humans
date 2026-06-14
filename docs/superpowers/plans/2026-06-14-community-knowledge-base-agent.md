# Community Knowledge Base for the Agent — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the in-app agent a community-sourced FAQ corpus (Discord-extracted markdown vendored into `docs/community-kb/`) via an always-present index + an on-demand `fetch_community_faq` tool that carries a "not official" provenance caveat, and switch the agent's GitHub-backed caches from TTL-expiry to load-once-and-hold-in-RAM warmed at startup.

**Architecture:** A parallel instance of the agent's existing index + on-demand-fetch pattern (`AgentSectionDocReader` + `fetch_section_guide`). A new `CommunityFaqReader` reuses the shared `IGuideContentSource` (extended with one directory-listing method) to discover and read `docs/community-kb/*.md` dynamically, caching with no expiration. The preload corpus gains a community index block; the dispatcher gains a wrapped tool; a new non-blocking hosted service preloads all agent caches after `ApplicationStarted`.

**Tech Stack:** C# / .NET, ASP.NET Core, Octokit, `IMemoryCache`, xUnit (`[HumansFact]`/`[HumansTheory]`), AwesomeAssertions, NSubstitute.

**Reference spec:** `docs/superpowers/specs/2026-06-14-community-knowledge-base-agent-design.md`

---

## ⚠️ Approval gates (surface to Peter before/at PR)

- **Interface surface:** Task 1 adds one method (`ListMarkdownStemsAsync`) to the existing `IGuideContentSource`. Per `peters-hard-rules.md`, public/interface surface needs Peter's approval. This is reuse of the existing GitHub-fetch abstraction (not a new interface), but flag it.
- **Deviation from spec — health check omitted:** The spec proposed extending `AgentDocsHealthCheck` to probe `docs/community-kb/`. This plan **omits** that: the community folder lives in the same repo/branch and uses the same token/connector already probed by the `Shifts` section canary, so it adds no independent failure mode — and an empty `docs/community-kb/` (before Peter's first manual data pull) is a valid state, not a fault, which a naive probe would mis-report as Degraded. Confirm this omission is acceptable, or we add a list-succeeds (empty-is-healthy) probe.
- **No data vendored yet:** This plan creates `docs/community-kb/.gitkeep` only. The first batch of cleaned/translated files is Peter's manual pull (the review gate). The feature ships dormant-but-correct: empty folder → no community index block → tool returns "unknown topic". Nothing breaks before data lands.

---

## File Structure

**Create:**
- `src/Humans.Infrastructure/Services/Preload/CommunityFaqReader.cs` — discover + read + cache community KB files; provenance wrapper; index parsing.
- `src/Humans.Infrastructure/Services/Preload/AgentPreloadWarmupHostedService.cs` — non-blocking startup preload of agent caches.
- `docs/community-kb/.gitkeep` — make the folder exist so listing returns `[]` cleanly pre-data.
- `tests/Humans.Application.Tests/Agent/CommunityFaqReaderTests.cs`
- `tests/Humans.Application.Tests/Agent/AgentPreloadWarmupHostedServiceTests.cs`

**Modify:**
- `src/Humans.Application/Interfaces/IGuideContentSource.cs` — add `ListMarkdownStemsAsync`.
- `src/Humans.Infrastructure/Services/GitHubGuideContentSource.cs` — implement it.
- `src/Humans.Application/Constants/AgentToolNames.cs` — add `FetchCommunityFaq`.
- `src/Humans.Infrastructure/Services/Agent/AgentPromptAssembler.cs` — tool definition + system-prompt rule.
- `src/Humans.Infrastructure/Services/Agent/AgentToolDispatcher.cs` — constructor dep + dispatch case.
- `src/Humans.Infrastructure/Services/Preload/AgentPreloadCorpusBuilder.cs` — community index block + no-expiry cache.
- `src/Humans.Infrastructure/Services/Preload/AgentSectionDocReader.cs` — no-expiry cache, drop settings dep.
- `src/Humans.Infrastructure/Services/Preload/AgentFeatureSpecReader.cs` — no-expiry cache, drop settings dep.
- `src/Humans.Web/Extensions/Sections/AgentSectionExtensions.cs` — register reader + hosted service.
- Test doubles implementing `IGuideContentSource` (Task 1) and reader/builder constructions (Task 5).

**Build/verify commands (run from the worktree root):**
- Build: `dotnet build Humans.slnx -v quiet`
- Test (whole agent area): `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Humans.Application.Tests.Agent"`
- Single test: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CommunityFaqReaderTests.<name>"`

> `-v quiet` is required (see `memory/process/dotnet-verbosity-quiet.md`). Commit after each task. Push every 2–3 tasks.

---

## Task 1: Add directory listing to `IGuideContentSource`

**Files:**
- Modify: `src/Humans.Application/Interfaces/IGuideContentSource.cs`
- Modify: `src/Humans.Infrastructure/Services/GitHubGuideContentSource.cs`
- Modify (test doubles): `tests/Humans.Application.Tests/Agent/AgentSectionDocReaderTests.cs`, `tests/Humans.Application.Tests/Agent/AgentToolDispatcherTests.cs`, `tests/Humans.Application.Tests/Agent/AgentPreloadCorpusBuilderTests.cs`

The Octokit `GetAllContentsByRef` listing call only exists inside `GitHubLegalDocumentConnector` (a Legal-section type) and on the raw client inside `GitHubGuideContentSource`. The agent must not depend on a Legal-section connector, so we add listing to the shared `IGuideContentSource` that already abstracts GitHub fetches for the guide and agent readers.

- [ ] **Step 1: Add the interface method**

In `src/Humans.Application/Interfaces/IGuideContentSource.cs`, add inside the interface body (after the existing `GetMarkdownAsync(folderPath, ...)` method):

```csharp
    /// <summary>
    /// Lists the markdown file stems (filename without the <c>.md</c> extension) in a folder
    /// inside the configured Humans repo/branch (e.g. <c>docs/community-kb</c>). Returns an
    /// empty list when the folder is absent. Used for dynamically-discovered corpora whose
    /// file set changes without a code change.
    /// </summary>
    Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Implement it in `GitHubGuideContentSource`**

In `src/Humans.Infrastructure/Services/GitHubGuideContentSource.cs`, add this method after `GetMarkdownAsync(folderPath, fileStem, ...)`:

```csharp
    public async Task<IReadOnlyList<string>> ListMarkdownStemsAsync(
        string folderPath, CancellationToken cancellationToken = default)
    {
        var settings = _guideSettings.Value;
        try
        {
            var contents = await _client.Repository.Content.GetAllContentsByRef(
                settings.Owner,
                settings.Repository,
                folderPath.TrimEnd('/'),
                settings.Branch);

            return contents
                .Where(c => c.Type == ContentType.File &&
                            c.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name[..^3]) // strip ".md"
                .ToList();
        }
        catch (NotFoundException)
        {
            _logger.LogWarning("Folder not found in GitHub: {FolderPath}", folderPath);
            return [];
        }
    }
```

`ContentType` and `NotFoundException` come from the already-imported `Octokit` namespace.

- [ ] **Step 3: Satisfy the new interface member in hand-rolled test doubles**

Add this method to each hand-rolled `IGuideContentSource` test double so the test project compiles. These doubles don't exercise listing, so an empty list is correct:

In `AgentSectionDocReaderTests.cs` (class `FakeSource`), `AgentToolDispatcherTests.cs` (class `StubGuideSource`), and `AgentPreloadCorpusBuilderTests.cs` (class `StubSource`), add:

```csharp
        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
```

- [ ] **Step 4: Build — fix any remaining implementers the compiler flags**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS. If the build reports `CS0535` ("does not implement interface member ... ListMarkdownStemsAsync") for any other `IGuideContentSource` implementer (e.g. in `GuideContentServiceTests` or `AgentServiceTests`), add the same one-line stub from Step 3 to that double, then rebuild. Substitute-based doubles (`Substitute.For<IGuideContentSource>()`) need no change.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(agent): add ListMarkdownStemsAsync to IGuideContentSource"
```

---

## Task 2: `CommunityFaqReader`

**Files:**
- Create: `src/Humans.Infrastructure/Services/Preload/CommunityFaqReader.cs`
- Create: `docs/community-kb/.gitkeep`
- Test: `tests/Humans.Application.Tests/Agent/CommunityFaqReaderTests.cs`

- [ ] **Step 1: Create the empty corpus folder**

Create `docs/community-kb/.gitkeep` (empty file) so the folder exists in the repo and `ListMarkdownStemsAsync("docs/community-kb")` returns `[]` rather than logging a not-found until Peter's first data pull.

- [ ] **Step 2: Write the failing tests**

Create `tests/Humans.Application.Tests/Agent/CommunityFaqReaderTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class CommunityFaqReaderTests
{
    private const string GeneralBody =
        "# General & Community — NCA\nLast updated: 2026-02-01 · windows merged through 2026-02-01\n\n## Overview\nWhat the NCA is and how to join.\n\n## FAQ\n**Q?**\nA.";

    [HumansFact]
    public async Task ListTopicsAsync_parses_title_date_and_overview_summary()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);

        entries.Should().ContainSingle();
        var e = entries[0];
        e.Topic.Should().Be("FAQ-general");
        e.Title.Should().Be("General & Community — NCA");
        e.LastUpdated.Should().Contain("2026-02-01");
        e.Summary.Should().Be("What the NCA is and how to join.");
    }

    [HumansFact]
    public async Task ListTopicsAsync_falls_back_to_title_when_no_overview()
    {
        var source = new FakeSource { Files = { ["bare"] = "# Bare Title\n\nNo overview here." } };
        var reader = MakeReader(source);

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);

        entries[0].Summary.Should().Be("Bare Title");
    }

    [HumansFact]
    public async Task ReadAsync_returns_body_for_a_discovered_topic()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var body = await reader.ReadAsync("FAQ-general", TestContext.Current.CancellationToken);

        body.Should().Be(GeneralBody);
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_for_unknown_topic()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var body = await reader.ReadAsync("does-not-exist", TestContext.Current.CancellationToken);

        body.Should().BeNull();
    }

    [HumansTheory]
    [InlineData("../secrets")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task ReadAsync_rejects_unsafe_topic_keys(string topic)
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var body = await reader.ReadAsync(topic, TestContext.Current.CancellationToken);

        body.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadAsync_caches_the_doc_fetch()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        await reader.ListTopicsAsync(TestContext.Current.CancellationToken); // warms via index parse
        await reader.ReadAsync("FAQ-general", TestContext.Current.CancellationToken);
        await reader.ReadAsync("FAQ-general", TestContext.Current.CancellationToken);

        // One raw fetch per file across list + reads.
        source.RawFetches["FAQ-general"].Should().Be(1);
    }

    [HumansFact]
    public void WrapWithProvenance_prepends_an_unofficial_header_with_the_date()
    {
        var wrapped = CommunityFaqReader.WrapWithProvenance(GeneralBody);

        wrapped.Should().StartWith("SOURCE: community Discord FAQ");
        wrapped.Should().Contain("NOT official");
        wrapped.Should().Contain("2026-02-01");
        wrapped.Should().Contain("not be official");
        wrapped.Should().Contain(GeneralBody);
    }

    [HumansFact]
    public async Task ListTopicsAsync_returns_empty_when_folder_absent()
    {
        var reader = MakeReader(new FakeSource()); // no files

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);

        entries.Should().BeEmpty();
    }

    private static CommunityFaqReader MakeReader(FakeSource source) =>
        new(source, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CommunityFaqReader>.Instance);

    private sealed class FakeSource : IGuideContentSource
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> RawFetches { get; } = new(StringComparer.Ordinal);

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
        {
            if (!Files.TryGetValue(fileStem, out var body))
                throw new NotFoundException("missing", System.Net.HttpStatusCode.NotFound);
            RawFetches[fileStem] = RawFetches.GetValueOrDefault(fileStem) + 1;
            return Task.FromResult(body);
        }

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Files.Keys.ToList());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CommunityFaqReaderTests"`
Expected: FAIL (compile error — `CommunityFaqReader` does not exist).

- [ ] **Step 4: Implement `CommunityFaqReader`**

Create `src/Humans.Infrastructure/Services/Preload/CommunityFaqReader.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CommunityFaqReaderTests"`
Expected: PASS (8 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(agent): add CommunityFaqReader for the docs/community-kb corpus"
```

---

## Task 3: The `fetch_community_faq` tool

**Files:**
- Modify: `src/Humans.Application/Constants/AgentToolNames.cs`
- Modify: `src/Humans.Infrastructure/Services/Agent/AgentPromptAssembler.cs`
- Modify: `src/Humans.Infrastructure/Services/Agent/AgentToolDispatcher.cs`
- Test: `tests/Humans.Application.Tests/Agent/AgentToolDispatcherTests.cs`

- [ ] **Step 1: Write the failing dispatcher tests**

In `tests/Humans.Application.Tests/Agent/AgentToolDispatcherTests.cs`, add these two tests (place them before the `MakeDispatcher` helper):

```csharp
    [HumansFact]
    public async Task FetchCommunityFaq_returns_wrapped_body_for_known_topic()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.FetchCommunityFaq, """{"topic":"FAQ-general"}"""),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        result.Content.Should().StartWith("SOURCE: community Discord FAQ");
        result.Content.Should().Contain("NOT official");
    }

    [HumansFact]
    public async Task FetchCommunityFaq_returns_error_for_unknown_topic()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.FetchCommunityFaq, """{"topic":"nope"}"""),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Unknown community FAQ topic");
    }
```

Update `StubGuideSource` (in this same test file) so the community reader discovers one file. Replace the `StubGuideSource` class with:

```csharp
    private sealed class StubGuideSource : Humans.Application.Interfaces.IGuideContentSource
    {
        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\nLast updated: 2026-02-01\n\n## Overview\nStub.");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                folderPath == Humans.Infrastructure.Services.Preload.CommunityFaqReader.FolderPath
                    ? ["FAQ-general"]
                    : []);
    }
```

Update the `MakeDispatcher` helper to construct and pass a `CommunityFaqReader`. Replace the reader-construction block and the `return new AgentToolDispatcher(...)` call with:

```csharp
        var community = new Humans.Infrastructure.Services.Preload.CommunityFaqReader(
            source, cache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Humans.Infrastructure.Services.Preload.CommunityFaqReader>.Instance);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Humans.Infrastructure.Services.Agent.AgentToolDispatcher>.Instance;
        return new Humans.Infrastructure.Services.Agent.AgentToolDispatcher(
            sections,
            features,
            community,
            auditViewer ?? new StubAuditViewer(),
            shiftView ?? Substitute.For<Interfaces.Shifts.IShiftView>(),
            shiftManagement ?? Substitute.For<Interfaces.Shifts.IShiftManagementService>(),
            logger);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AgentToolDispatcherTests.FetchCommunityFaq"`
Expected: FAIL (compile error — `AgentToolNames.FetchCommunityFaq` and the dispatcher's `community` parameter do not exist yet).

- [ ] **Step 3: Add the tool name**

In `src/Humans.Application/Constants/AgentToolNames.cs`, add the const and include it in `All`:

```csharp
    public const string FetchCommunityFaq = "fetch_community_faq";
```

and add `FetchCommunityFaq,` to the `All` `HashSet` initializer.

- [ ] **Step 4: Add the tool definition + system-prompt rule**

In `src/Humans.Infrastructure/Services/Agent/AgentPromptAssembler.cs`, add a new entry to the `BuildToolDefinitions()` collection (after the `FetchSectionGuide` entry):

```csharp
        new(Name: AgentToolNames.FetchCommunityFaq,
            Description: "Fetch a community-sourced FAQ topic by its topic key from the Community FAQ index. This content is crowd-sourced from the community Discord and is NOT official — it may be outdated or inaccurate. Prefer fetch_section_guide when a section guide covers the question.",
            JsonSchema: """{"type":"object","properties":{"topic":{"type":"string"}},"required":["topic"]}"""),
```

In the same file, in the `SystemPromptHeader` raw string, add a workflow step and a rule. After workflow step 4 (`Once you have the section docs, answer...`), add:

```
        5. For community/event/history questions not covered by a section guide (what the NCA is, the event, comms, community practices), check the "Community FAQ" index below and call `fetch_community_faq` with `topic=<key>`. This source is community-sourced and unofficial — see the rules.
```

And in the Rules block, add this bullet (after the "Answer ONLY from..." rule):

```
        - Community FAQ (fetched via `fetch_community_faq`) is crowd-sourced from the community Discord and is NOT official; it may be outdated or inaccurate. Prefer authoritative section guides when they cover the question. Whenever your answer relies on the community FAQ, tell the user it comes from community discussion and may not be official.
```

- [ ] **Step 5: Add the dispatcher dependency + case**

In `src/Humans.Infrastructure/Services/Agent/AgentToolDispatcher.cs`, add `CommunityFaqReader community` to the primary constructor parameter list (after `AgentFeatureSpecReader features,`):

```csharp
public sealed class AgentToolDispatcher(
    AgentSectionDocReader sections,
    AgentFeatureSpecReader features,
    CommunityFaqReader community,
    IAuditViewerService auditViewer,
    IShiftView shiftView,
    IShiftManagementService shiftManagement,
    ILogger<AgentToolDispatcher> logger) : IAgentToolDispatcher
```

Add this `case` to the `switch (call.Name)` block (after the `FetchSectionGuide` case):

```csharp
                case AgentToolNames.FetchCommunityFaq:
                    {
                        var topic = args.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : "";
                        var body = await community.ReadAsync(topic, cancellationToken);
                        return body is null
                            ? new AnthropicToolResult(call.Id, string.Create(CultureInfo.InvariantCulture, $"Unknown community FAQ topic: {topic}"), IsError: true)
                            : new AnthropicToolResult(call.Id, CommunityFaqReader.WrapWithProvenance(body), IsError: false);
                    }
```

(`CommunityFaqReader` is in `Humans.Infrastructure.Services.Preload`, already imported via the existing `using Humans.Infrastructure.Services.Preload;`.)

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AgentToolDispatcherTests"`
Expected: PASS (all existing dispatcher tests + the 2 new ones). If a test elsewhere asserts the tool-definition count (e.g. `BuildToolDefinitions().Count.Should().Be(5)`), bump it to `6`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(agent): add fetch_community_faq tool with provenance wrapper"
```

---

## Task 4: Community index in the preload corpus

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Preload/AgentPreloadCorpusBuilder.cs`
- Test: `tests/Humans.Application.Tests/Agent/AgentPreloadCorpusBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

In `tests/Humans.Application.Tests/Agent/AgentPreloadCorpusBuilderTests.cs`, add this test:

```csharp
    [HumansFact]
    public async Task Index_includes_community_faq_block_when_files_exist()
    {
        var builder = MakeBuilder(communityFiles: ["FAQ-general"]);
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);

        text.Should().Contain("Community FAQ");
        text.Should().Contain("**FAQ-general**");
        text.Should().Contain("unofficial");
    }

    [HumansFact]
    public async Task Index_omits_community_block_when_no_files()
    {
        var builder = MakeBuilder(communityFiles: []);
        var text = await builder.BuildAsync(AgentPreloadConfig.Tier2, Xunit.TestContext.Current.CancellationToken);

        text.Should().NotContain("Community FAQ");
    }
```

Update the `MakeBuilder` helper and `StubSource` in this file to support community files. Replace `MakeBuilder` and `StubSource` with:

```csharp
    private static IAgentPreloadCorpusBuilder MakeBuilder(IReadOnlyList<string>? communityFiles = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var source = new StubSource { CommunityFiles = communityFiles ?? [] };
        var reader = new AgentSectionDocReader(
            source, cache, NullLogger<AgentSectionDocReader>.Instance);
        var community = new CommunityFaqReader(source, cache, NullLogger<CommunityFaqReader>.Instance);
        return new AgentPreloadCorpusBuilder(reader, community, cache);
    }

    private sealed class StubSource : IGuideContentSource
    {
        public IReadOnlyList<string> CommunityFiles { get; init; } = [];

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                folderPath == CommunityFaqReader.FolderPath
                    ? $"# {fileStem} title\nLast updated: 2026-02-01\n\n## Overview\nCommunity summary for {fileStem}."
                    : $"# {fileStem}\n\nTagline for {fileStem}.");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(folderPath == CommunityFaqReader.FolderPath ? CommunityFiles : []);
    }
```

> Note: this changes the `AgentSectionDocReader` construction to the no-settings constructor introduced in Task 5. If you implement Task 4 before Task 5, temporarily keep the `Options.Create(new GuideSettings { CacheTtlHours = 6 })` argument and the `using` lines, then remove them in Task 5. Recommended: do Task 5 first if convenient, but either order builds as long as the reader constructor and its call sites agree.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AgentPreloadCorpusBuilderTests.Index_includes_community"`
Expected: FAIL (compile error — `AgentPreloadCorpusBuilder` has no `community` constructor parameter yet).

- [ ] **Step 3: Add the dependency + index block**

In `src/Humans.Infrastructure/Services/Preload/AgentPreloadCorpusBuilder.cs`, add `CommunityFaqReader community` to the primary constructor (before the optional `augmentor`):

```csharp
public sealed class AgentPreloadCorpusBuilder(
    AgentSectionDocReader sections,
    CommunityFaqReader community,
    IMemoryCache cache,
    IAgentPreloadAugmentor? augmentor = null) : IAgentPreloadCorpusBuilder
```

In `BuildAsync`, after the augmentor block (after the `if (augmentor is not null) { ... }` block, before `var result = sb.ToString();`), add:

```csharp
        var communityEntries = await community.ListTopicsAsync(cancellationToken);
        if (communityEntries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Community FAQ (community-sourced — unofficial, may be outdated)");
            sb.AppendLine();
            sb.AppendLine("Crowd-sourced answers from the community Discord. Fetch a topic on demand with the `fetch_community_faq` tool (topic=<key>). Always tell the user these answers are community discussion, not official.");
            sb.AppendLine();
            foreach (var entry in communityEntries)
            {
                sb.Append("- **").Append(entry.Topic).Append("** — ").Append(entry.Summary);
                if (entry.LastUpdated is not null)
                    sb.Append(" (last updated ").Append(entry.LastUpdated).Append(')');
                sb.AppendLine();
            }
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AgentPreloadCorpusBuilderTests"`
Expected: PASS (existing tests + the 2 new ones). The token-budget test still passes because the empty-folder default adds no community block.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(agent): add community FAQ index to the preload corpus"
```

---

## Task 5: Drop the TTLs — hold agent caches in RAM

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Preload/AgentSectionDocReader.cs`
- Modify: `src/Humans.Infrastructure/Services/Preload/AgentFeatureSpecReader.cs`
- Modify: `src/Humans.Infrastructure/Services/Preload/AgentPreloadCorpusBuilder.cs`
- Test: `tests/Humans.Application.Tests/Agent/AgentSectionDocReaderTests.cs`, `AgentToolDispatcherTests.cs`, `AgentPreloadCorpusBuilderTests.cs`

These caches hold GitHub-backed content that only changes at release; a release restarts the process. So they should never expire. Remove the TTL and the now-unused `GuideSettings` dependency from both readers, and change the corpus builder's 30-minute TTL to no-expiry.

- [ ] **Step 1: Rewrite `AgentSectionDocReader` caching**

In `src/Humans.Infrastructure/Services/Preload/AgentSectionDocReader.cs`:

Remove the `using Humans.Infrastructure.Configuration;` and `using Microsoft.Extensions.Options;` lines. Change the constructor to drop `settings`:

```csharp
public sealed class AgentSectionDocReader(
    IGuideContentSource source,
    IMemoryCache cache,
    ILogger<AgentSectionDocReader> logger)
```

Add a shared no-expiry options field (after the `Whitelist` field):

```csharp
    // No expiration + NeverRemove: GitHub-backed content that only changes at release.
    // Loaded once (startup warm-up or first call) and held for the process lifetime.
    private static readonly MemoryCacheEntryOptions HoldForever =
        new() { Priority = CacheItemPriority.NeverRemove };
```

Replace the two lines inside the `try` block:

```csharp
            var ttl = TimeSpan.FromHours(Math.Max(1, settings.Value.CacheTtlHours));
            cache.Set(cacheKey, body, new MemoryCacheEntryOptions { SlidingExpiration = ttl });
```

with:

```csharp
            cache.Set(cacheKey, body, HoldForever);
```

- [ ] **Step 2: Rewrite `AgentFeatureSpecReader` caching the same way**

In `src/Humans.Infrastructure/Services/Preload/AgentFeatureSpecReader.cs`: remove the `using Humans.Infrastructure.Configuration;` and `using Microsoft.Extensions.Options;` lines, drop the `IOptions<GuideSettings> settings` constructor parameter, add the same `HoldForever` static field (after the `CacheKeyPrefix` const), and replace the `var ttl = ...; cache.Set(cacheKey, body, new MemoryCacheEntryOptions { SlidingExpiration = ttl });` pair with `cache.Set(cacheKey, body, HoldForever);`.

- [ ] **Step 3: Change the corpus builder TTL to no-expiry**

In `src/Humans.Infrastructure/Services/Preload/AgentPreloadCorpusBuilder.cs`, replace:

```csharp
        cache.Set(cacheKey, result, TimeSpan.FromMinutes(30));
```

with:

```csharp
        cache.Set(cacheKey, result, new MemoryCacheEntryOptions { Priority = CacheItemPriority.NeverRemove });
```

(Add `using Microsoft.Extensions.Caching.Memory;` if not already present — it is, since the builder already uses `IMemoryCache`.)

- [ ] **Step 4: Fix the reader constructions in tests**

The reader constructors no longer take `GuideSettings`. Update each construction:

- `AgentSectionDocReaderTests.cs` — `MakeReader`: change to `new(source, new MemoryCache(new MemoryCacheOptions()), NullLogger<AgentSectionDocReader>.Instance);` and remove the now-unused `using Humans.Infrastructure.Configuration;` and `using Microsoft.Extensions.Options;`.
- `AgentToolDispatcherTests.cs` — `MakeDispatcher`: remove the `guideSettings` local and pass only `(source, cache, NullLogger<...>.Instance)` to both `AgentSectionDocReader` and `AgentFeatureSpecReader`; remove the now-unused `GuideSettings`/`Options` references.
- `AgentPreloadCorpusBuilderTests.cs` — already updated in Task 4 Step 1 to the no-settings constructor; if you did Task 5 first, apply that constructor shape now.

- [ ] **Step 5: Build and run the agent tests**

Run: `dotnet build Humans.slnx -v quiet`
Then: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Humans.Application.Tests.Agent"`
Expected: PASS. The existing `ReadAsync_caches_successful_fetch` test still passes (caching behaviour is unchanged; only expiration is removed).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(agent): hold doc caches in RAM with no TTL"
```

---

## Task 6: Non-blocking startup preload

**Files:**
- Create: `src/Humans.Infrastructure/Services/Preload/AgentPreloadWarmupHostedService.cs`
- Modify: `src/Humans.Web/Extensions/Sections/AgentSectionExtensions.cs`
- Test: `tests/Humans.Application.Tests/Agent/AgentPreloadWarmupHostedServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Humans.Application.Tests/Agent/AgentPreloadWarmupHostedServiceTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentPreloadWarmupHostedServiceTests
{
    [HumansFact]
    public async Task WarmCachesAsync_builds_every_tier_and_lists_community_topics()
    {
        var corpus = Substitute.For<IAgentPreloadCorpusBuilder>();
        corpus.BuildAsync(Arg.Any<AgentPreloadConfig>(), Arg.Any<CancellationToken>())
            .Returns("corpus");

        var source = new ListCountingSource();
        var community = new CommunityFaqReader(source, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CommunityFaqReader>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(corpus);
        services.AddSingleton(community);
        await using var sp = services.BuildServiceProvider();

        await AgentPreloadWarmupHostedService.WarmCachesAsync(sp, TestContext.Current.CancellationToken);

        await corpus.Received(1).BuildAsync(AgentPreloadConfig.Tier1, Arg.Any<CancellationToken>());
        await corpus.Received(1).BuildAsync(AgentPreloadConfig.Tier2, Arg.Any<CancellationToken>());
        source.ListCalls.Should().Be(1);
    }

    [HumansFact]
    public async Task WarmAsync_swallows_exceptions()
    {
        var corpus = Substitute.For<IAgentPreloadCorpusBuilder>();
        corpus.BuildAsync(Arg.Any<AgentPreloadConfig>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("github down"));

        var community = new CommunityFaqReader(new ListCountingSource(),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<CommunityFaqReader>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(corpus);
        services.AddSingleton(community);
        await using var sp = services.BuildServiceProvider();
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();

        var svc = new AgentPreloadWarmupHostedService(
            Substitute.For<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
            scopes,
            NullLogger<AgentPreloadWarmupHostedService>.Instance);

        // Must not throw despite the corpus builder failing.
        await svc.WarmAsync();
    }

    private sealed class ListCountingSource : IGuideContentSource
    {
        public int ListCalls { get; private set; }

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}\n\n## Overview\nx.");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AgentPreloadWarmupHostedServiceTests"`
Expected: FAIL (compile error — `AgentPreloadWarmupHostedService` does not exist).

- [ ] **Step 3: Implement the hosted service**

Create `src/Humans.Infrastructure/Services/Preload/AgentPreloadWarmupHostedService.cs`:

```csharp
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Preload;

/// <summary>
/// Loads the agent's GitHub-backed caches into RAM once the app is serving, so the first
/// real request never pays the cold-fetch latency. Hooks <c>ApplicationStarted</c> and runs
/// fire-and-forget — it never blocks startup. The caches have no TTL (see the readers and
/// <see cref="AgentPreloadCorpusBuilder"/>), so there is nothing to re-warm: restart (i.e. a
/// release) is the refresh. Building the Tier2 corpus warms every section guide as a side
/// effect; listing community topics warms every community KB file.
/// </summary>
public sealed class AgentPreloadWarmupHostedService(
    IHostApplicationLifetime lifetime,
    IServiceScopeFactory scopes,
    ILogger<AgentPreloadWarmupHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run after startup completes so the host is never blocked on GitHub I/O.
        lifetime.ApplicationStarted.Register(() => _ = WarmAsync());
        return Task.CompletedTask;
    }

    internal async Task WarmAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            await WarmCachesAsync(scope.ServiceProvider, CancellationToken.None);
            logger.LogInformation("Agent preload caches warmed");
        }
        catch (Exception ex)
        {
            // A warm-up miss must never crash the host; the lazy fetch paths still populate
            // the caches on first use. Logged per memory/code/always-log-problems.md.
            logger.LogWarning(ex, "Agent preload warm-up failed; caches will populate lazily on first use");
        }
    }

    internal static async Task WarmCachesAsync(IServiceProvider sp, CancellationToken cancellationToken)
    {
        var corpus = sp.GetRequiredService<IAgentPreloadCorpusBuilder>();
        foreach (var config in Enum.GetValues<AgentPreloadConfig>())
            await corpus.BuildAsync(config, cancellationToken);

        // ListTopicsAsync reads every community doc to build the index, which warms the
        // per-file cache that fetch_community_faq serves from.
        var community = sp.GetRequiredService<CommunityFaqReader>();
        await community.ListTopicsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: Register the reader + hosted service in DI**

In `src/Humans.Web/Extensions/Sections/AgentSectionExtensions.cs`:

Add the reader registration next to the other singleton readers (after `services.AddSingleton<AgentFeatureSpecReader>();`):

```csharp
        services.AddSingleton<CommunityFaqReader>();
```

Add the hosted service next to the existing one (after `services.AddHostedService<AgentSettingsStoreWarmupHostedService>();`):

```csharp
        services.AddHostedService<AgentPreloadWarmupHostedService>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AgentPreloadWarmupHostedServiceTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Full build + agent test sweep**

Run: `dotnet build Humans.slnx -v quiet`
Then: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Humans.Application.Tests.Agent"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(agent): preload doc caches at startup (non-blocking)"
```

---

## Task 7: Architecture tests + full suite + push

**Files:** none (verification only), unless an architecture test needs a new entry.

- [ ] **Step 1: Run architecture tests**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Architecture"`
Expected: PASS. `CommunityFaqReader` and `AgentPreloadWarmupHostedService` live in `Humans.Infrastructure.Services.Preload` and depend only on `IGuideContentSource` + `IMemoryCache` + logging — no vertical-section repository or `DbContext` reference, so no new architecture violation. If `AgentArchitectureTests` enumerates expected agent types or tool names, add the new ones there.

- [ ] **Step 2: Full test suite**

Run: `dotnet test Humans.slnx -v quiet`
Expected: PASS (all green).

- [ ] **Step 3: Push the branch**

```bash
git push
```

---

## Self-Review (completed during planning)

**Spec coverage:**
- Vendor into `docs/community-kb/`, read via existing connector/token → Tasks 1, 2 (`.gitkeep` + reader using `IGuideContentSource`).
- Dynamic discovery → Task 1 (`ListMarkdownStemsAsync`) + Task 2 (`ListTopicsAsync`).
- Index in preload corpus → Task 4.
- `fetch_community_faq` tool, separate from `fetch_section_guide` → Task 3.
- Provenance: system-prompt rule + per-result wrapper (option C) → Task 3 (Steps 4 & 5).
- No TTL, preload + hold in RAM, no re-warm timer, restart = refresh, `/Guide` untouched → Tasks 5, 6.
- Confidential folder never touched → reader only ever reads `docs/community-kb` (the public vendored copy); confidential files are never copied into this repo.
- Health-check extension → **deliberately omitted** (see Approval gates) — confirm with Peter.

**Placeholder scan:** none — every code step shows complete code; verification steps give exact commands and expected results.

**Type consistency:** `CommunityFaqReader` (ctor `source, cache, logger`), `IndexEntry(Topic, Title, LastUpdated, Summary)`, `WrapWithProvenance(string)`, `ListTopicsAsync` / `ReadAsync`, `AgentToolNames.FetchCommunityFaq`, dispatcher param `community`, corpus-builder param order `(sections, community, cache, augmentor?)`, hosted-service `WarmAsync` / `WarmCachesAsync` — all used consistently across tasks and tests.
