# Community knowledge base for the agent

**Status:** Draft for review
**Date:** 2026-06-14
**Branch:** `feat/community-kb-agent`
**Sections touched:** Agent (horizontal). New on-demand corpus + index + a startup warm-up. No DB, no vertical-section data.

## Context

The agent today answers from authoritative app docs. It already runs the exact pattern this feature needs:

- `AgentPreloadCorpusBuilder` (singleton) assembles a small **section index** — one tagline per section — plus the access matrix, glossaries, route map, and FAQ. The assembled string is cached under `agent:preload:{config}` with a **30-minute absolute TTL** and inlined into the Anthropic system prompt (server-side prompt-cached by `AnthropicClient`).
- The model reads the index, picks a section, and calls **`fetch_section_guide`** → `AgentSectionDocReader.ReadAsync(key)` → `IGuideContentSource.GetMarkdownAsync("docs/sections", key)` (Octokit raw fetch from `nobodies-collective/Humans`, memory-cached, sliding TTL `GuideSettings.CacheTtlHours`). `fetch_feature_spec` works the same way against `docs/features/`.

So an index + on-demand fetch tool already exists; this feature is a **parallel instance of it** pointed at a new corpus, plus the startup warm-up Peter asked for.

Separately, `nca-discord/knowledge-base/` holds knowledge extracted from the community Discord by an offline pipeline. Its `public/` folder contains clean, topic-partitioned markdown (`FAQ-general.md`, `FAQ-comms.md`, `FAQ-site.md`, `RETROSPECTIVE-2027.md`, …), each shaped as: `# Title` / `Last updated: …` / `## Overview` / `## Key facts` / `## FAQ` (bold question, answer). The first window is ~4 public docs; ~4 months of further windows are coming — expect this to grow ~100×, with ~50+ facts per topic file. A sibling `confidential/` folder (board dossiers, internal docs) is **out of bounds**.

This knowledge is community-sourced and **not 100% accurate** — the agent must surface that whenever it relies on it.

## Goals

1. Give the agent a **community FAQ corpus** it can consult: an always-present index of available topics + an on-demand fetch tool, mirroring `fetch_section_guide`.
2. **Provenance caveat** on every use: the agent tells users community knowledge is unofficial and may be outdated.
3. **Warm the agent's GitHub-backed caches at startup** (non-blocking) so the first real request after a deploy doesn't pay the cold-cache latency — replacing the current lazy-on-first-request behaviour.

## Non-goals

- **No in-app ingestion.** Cleanup, Discord-username→display-name translation, and the copy from `nca-discord/public/` into this repo are a **manual periodic step Peter runs** (the review/accuracy gate). The app only reads the committed result. The ingestion tooling is out of scope for this spec.
- **No DB / entity / repository.** This is GitHub-backed content read into `IMemoryCache`, exactly like the guide and section docs. No vertical-section data, so no repository or `IServiceRead` surface.
- **No embeddings / vector search.** The agent routes by reading index descriptions, as it already does for section guides. Keyword/topic granularity is sufficient at this scale.
- **No auto-sync from nca-discord**, no topic→team mapping table, no admin UI for the corpus.
- **The `confidential/` folder is never copied into this repo** and the reader never reads outside `docs/community-kb/`.

## Design decision — vendor into this repo, reuse the existing reader pattern

**Source of truth the app reads:** `docs/community-kb/` in the **Humans repo** (`origin`/`upstream`), populated by Peter's manual pull-clean-translate step. Rejected reading `nca-discord/public/` directly: it would couple production to a personal repo, bypass the review gate, and risk the adjacent `confidential/` folder. Vendoring reuses the existing `IGuideContentSource` Octokit fetch and `GitHub:AccessToken` end-to-end with zero new auth.

**Discovery:** dynamic directory listing of `docs/community-kb/`, using the pattern `GitHubLegalDocumentConnector.DiscoverLanguageFilesAsync` already uses (`GetAllContentsByRef`). Rejected the hardcoded `GuideFiles.cs`-style list: at ~100× growth, new topic files must appear without a code change. New files become available to the agent the moment Peter commits them.

**Separate tool, not folded into `fetch_section_guide`:** authoritative section guides and community FAQ must never blur. A distinct tool lets the result carry a provenance wrapper and keeps the index labelled as community-sourced.

## Component 1 — `CommunityFaqReader` (Infrastructure / Services / Preload)

Mirror `AgentSectionDocReader` (same folder, same registration/interface shape — follow whatever that reader does exactly; do not invent new surface). Responsibilities:

- **`ListTopicsAsync()`** — dynamic directory listing of `docs/community-kb/` via the Octokit path used by `GitHubLegalDocumentConnector`. Returns the set of topic keys (filename without `.md`) plus, for each, a parsed **index entry**: the `# H1` title, the `Last updated:` date, and the first non-empty paragraph of `## Overview` (fallback: just the H1). Memory-cached (sliding TTL `GuideSettings.CacheTtlHours`, key prefix `community-kb:index`).
- **`ReadAsync(topic)`** — validate `topic` against `[A-Za-z0-9\-_]+` (path-traversal guard, like `AgentFeatureSpecReader`) **and** against the discovered topic set; fetch `docs/community-kb/{topic}.md` via `IGuideContentSource.GetMarkdownAsync`; memory-cache per file (key prefix `community-kb:`). Returns the raw markdown.

No DbContext, no cross-section call. Pure GitHub-backed infrastructure service.

## Component 2 — the index (in the preloaded corpus)

In `AgentPreloadCorpusBuilder.BuildAsync`, after the section index, append a **Community FAQ index** block built from `CommunityFaqReader.ListTopicsAsync()`:

```
## Community FAQ (community-sourced — unofficial, may be outdated)
- **comms** — Comms & website: open-source site, comms-lead structure, meeting-summary norm. (last updated 2026-02-01)
- **general** — What the NCA is, the event, joining Discord/newsletter, leads/volunteers apply via Humans. (last updated 2026-02-01)
- …
```

Included regardless of `AgentPreloadConfig` tier — the index is one line per file and negligible even at 100× (dozens of lines). Full docs are **never** inlined; they are fetched on demand. The block header itself states the corpus is unofficial, so the model is primed before it ever fetches.

## Component 3 — the `fetch_community_faq` tool (Application + Infrastructure)

1. `AgentToolNames` — add `public const string FetchCommunityFaq = "fetch_community_faq";` and include it in the `All` set.
2. `AgentPromptAssembler.BuildToolDefinitions()` — add `new AnthropicToolDefinition("fetch_community_faq", "Fetch a community-sourced FAQ topic (unofficial, may be outdated) by its topic key from the Community FAQ index.", """{"type":"object","properties":{"topic":{"type":"string"}},"required":["topic"]}""")`.
3. `AgentToolDispatcher.DispatchAsync()` — add `case AgentToolNames.FetchCommunityFaq:` that parses `topic`, calls `CommunityFaqReader.ReadAsync(topic)`, and returns the content **wrapped** with a provenance header:

   ```
   SOURCE: community Discord FAQ · NOT official · may be outdated · last updated {date}
   When you use anything below, tell the user it comes from community discussion and may not be official.

   {file markdown}
   ```

   Unknown/invalid topic → `AnthropicToolResult(IsError: true)` listing valid topics (same shape as the existing readers' error path).
4. Inject `CommunityFaqReader` into `AgentToolDispatcher`'s constructor.

## Component 4 — the caveat (Peter's option C)

- **Policy rule** appended to `AgentPromptAssembler.SystemPromptHeader`: community FAQ is crowd-sourced from Discord, may be outdated or inaccurate; when relying on it, tell the user it's community discussion and not official; prefer authoritative section guides/specs when they cover the question.
- **Per-result wrapper** (Component 3) guarantees the provenance is in front of the model even late in a long turn, when the system-prompt rule has faded.

## Component 5 — startup warm-up (the cold-cache fix)

Add a hosted service modelled on the existing `AgentSettingsStoreWarmupHostedService`. Hook `IHostApplicationLifetime.ApplicationStarted` and run the warm-up **fire-and-forget off the startup path** (startup is never blocked). It warms the **memory caches**:

- `AgentPreloadCorpusBuilder.BuildAsync` for each active `AgentPreloadConfig` — removes the first-request 30-minute cold miss.
- `CommunityFaqReader.ListTopicsAsync()` (the index).
- The full-document caches the agent fetches on demand — every `docs/sections/*` guide and every `docs/community-kb/*` file — so tool fetches are served from RAM.

Then **re-warm on a timer set just under the cache TTL** so the caches never lapse back to cold.

Key distinction this encodes: warming the **memory cache** makes fetches instant at **zero standing prompt-token cost** — only the small indexes are inlined into the prompt; full docs live in RAM and are served via the tools. That is what "keep the docs in RAM, serve them as a tool" means here, and why the memory cost is minimal.

Failures are swallowed and logged (a warm-up miss must never crash the host); the lazy paths still work, so a failed warm-up degrades to today's behaviour.

## Registration & health

- Register `CommunityFaqReader` and the warm-up hosted service in `AgentSectionExtensions` alongside the existing agent services (match lifetimes: readers singleton, hosted service via `AddHostedService`).
- Extend `AgentDocsHealthCheck` to include `docs/community-kb/` reachability (it already checks the agent's GitHub docs), so a broken corpus path surfaces in health rather than silently.

## Scale (100×) notes

- Standing prompt cost is bounded: only the one-line-per-file index is inlined; full files are fetched on demand and cached.
- Dynamic discovery → no per-file code churn as topics multiply.
- A single large topic file is returned whole by `fetch_community_faq`; fine at ~500 users / single server. If individual files later get unwieldy, the offline pipeline can split them — no app change needed.

## Testing

- `CommunityFaqReader`: topic validation (rejects traversal, rejects unknown topics), index parsing (H1 + Overview + last-updated, and the H1-only fallback), cache key isolation from the guide cache.
- `AgentToolDispatcher`: `fetch_community_faq` happy path returns wrapped content; invalid topic returns the error result.
- Provenance wrapper present in every successful community-FAQ tool result.
- Warm-up hosted service: does not block `ApplicationStarted`; a thrown warm-up does not crash the host.
- Architecture tests: the new reader and tool stay within Agent/Infrastructure and touch no vertical-section repository or DbContext.

## Open implementation questions (for the plan, not blocking)

- Whether `AgentSectionDocReader` is fronted by an interface; mirror it exactly for `CommunityFaqReader` either way.
- Exact re-warm interval (a timer just under `GuideSettings.CacheTtlHours` / the 30-min preload TTL).
