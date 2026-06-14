# Community knowledge base for the agent

**Status:** Draft for review
**Date:** 2026-06-14
**Branch:** `feat/community-kb-agent`
**Sections touched:** Agent (horizontal). New on-demand corpus + index + a startup warm-up. No DB, no vertical-section data.

## Context

The agent today answers from authoritative app docs. It already runs the exact pattern this feature needs:

- `AgentPreloadCorpusBuilder` (singleton) assembles a small **section index** — one tagline per section — plus the access matrix, glossaries, route map, and FAQ. The assembled string is cached under `agent:preload:{config}` with a **30-minute absolute TTL** and inlined into the Anthropic system prompt (server-side prompt-cached by `AnthropicClient`).
- The model reads the index, picks a section, and calls **`fetch_section_guide`** → `AgentSectionDocReader.ReadAsync(key)` → `IGuideContentSource.GetMarkdownAsync("docs/sections", key)` (Octokit raw fetch from `nobodies-collective/Humans`, memory-cached, sliding TTL `GuideSettings.CacheTtlHours`). `fetch_feature_spec` works the same way against `docs/features/`.

So an index + on-demand fetch tool already exists; this feature is a **parallel instance of it** pointed at a new corpus. The 30-minute preload TTL and the readers' sliding TTLs are also unnecessary — see Goal 3 and Component 5: the agent's content is GitHub-backed and the right model is preload-once-and-hold, not expire-and-refetch.

Separately, `nca-discord/knowledge-base/` holds knowledge extracted from the community Discord by an offline pipeline. Its `public/` folder contains clean, topic-partitioned markdown (`FAQ-general.md`, `FAQ-comms.md`, `FAQ-site.md`, `RETROSPECTIVE-2027.md`, …), each shaped as: `# Title` / `Last updated: …` / `## Overview` / `## Key facts` / `## FAQ` (bold question, answer). The first window is ~4 public docs; ~4 months of further windows are coming — expect this to grow ~100×, with ~50+ facts per topic file. A sibling `confidential/` folder (board dossiers, internal docs) is **out of bounds**.

This knowledge is community-sourced and **not 100% accurate** — the agent must surface that whenever it relies on it.

## Goals

1. Give the agent a **community FAQ corpus** it can consult: an always-present index of available topics + an on-demand fetch tool, mirroring `fetch_section_guide`.
2. **Provenance caveat** on every use: the agent tells users community knowledge is unofficial and may be outdated.
3. **Drop the TTLs on the agent's GitHub-backed caches and preload them once, early in startup (non-blocking), held in RAM for the process lifetime.** The current 30-minute preload TTL and the readers' sliding TTLs are pointless churn: the content is GitHub-backed and only effectively changes at release, and a release restarts the process. Restart *is* the refresh; nothing expires in between.

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

- **`ListTopicsAsync()`** — dynamic directory listing of `docs/community-kb/` via the Octokit path used by `GitHubLegalDocumentConnector`. Returns the set of topic keys (filename without `.md`) plus, for each, a parsed **index entry**: the `# H1` title, the `Last updated:` date, and the first non-empty paragraph of `## Overview` (fallback: just the H1). Held in RAM with **no expiration** (key prefix `community-kb:index`) — populated by the startup warm-up, refreshed only on restart.
- **`ReadAsync(topic)`** — validate `topic` against `[A-Za-z0-9\-_]+` (path-traversal guard, like `AgentFeatureSpecReader`) **and** against the discovered topic set; fetch `docs/community-kb/{topic}.md` via `IGuideContentSource.GetMarkdownAsync`; cache per file with **no expiration** (key prefix `community-kb:`). Returns the raw markdown.

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

## Component 5 — preload + hold in RAM (no TTL)

**Remove the TTLs from the agent's caches.** The `agent:preload:{config}` entry loses its 30-minute absolute TTL; the agent readers (`AgentSectionDocReader`, `AgentFeatureSpecReader`, and the new `CommunityFaqReader`) lose their sliding TTL. Agent cache entries are written with **no absolute/sliding expiration and `CacheItemPriority.NeverRemove`** so memory pressure can't evict them either — once loaded, they stay for the process lifetime. This is scoped to the **agent's** caches; the user-facing `/Guide` cache and its `POST /Guide/Refresh` admin path are left exactly as they are.

**Preload once, early, without gating startup.** Add a hosted service modelled on the existing `AgentSettingsStoreWarmupHostedService`. Hook `IHostApplicationLifetime.ApplicationStarted` and run the load **fire-and-forget off the startup path** (startup is never blocked). It populates, into RAM:

- `AgentPreloadCorpusBuilder.BuildAsync` for the active `AgentPreloadConfig` (a runtime admin tier-flip rebuilds lazily once, then is held).
- `CommunityFaqReader.ListTopicsAsync()` (the community index).
- The full documents the agent fetches on demand — every `docs/sections/*` guide and every `docs/community-kb/*` file — so tool fetches are served from RAM from the first request.

**No re-warm timer.** Nothing expires, so there is nothing to refresh on a schedule. Content changes ship in a release, and a release restarts the process, which re-runs the load — restart is the refresh. (No admin refresh endpoint for the agent; not asked for, and restart covers it.)

Key distinction this encodes: holding the docs **in RAM** makes fetches instant at **zero standing prompt-token cost** — only the small indexes are inlined into the prompt; full docs live in RAM and are served via the tools. That is what "keep the docs in RAM, serve them as a tool" means here, and why the memory cost is minimal.

Failures are swallowed and logged (a load miss must never crash the host); the lazy fetch paths still work as a fallback, so a failed preload only costs first-request latency, never correctness.

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
- Confirm the agent readers' cache entries are distinct from the user-facing `GuideContentService` (`guide:`) entries, so removing the agent TTLs leaves `/Guide` untouched. If any entry is shared, split it rather than changing `/Guide` behaviour.

## Addendum (2026-06-14) — separate KB repo + admin Reload

Two revisions adopted after the initial design, during implementation:

1. **Dedicated repo, not vendored into the code repo.** The corpus lives in a standalone public repo **`nobodies-collective/knowledge-base`** (files under `docs/community-kb/`), read via a second `IGuideContentSource` (`GitHubCommunityKbContentSource`) bound to `CommunityKbSettings` (`CommunityKb:Owner/Repository/Branch/AccessToken`, defaulting to that repo's `main`). Rationale: vendoring into the Humans repo would force every content drop through the two-remote prod-promotion flow and bloat code history/PRs with non-code churn at ~100× growth. A separate repo lets content ship on its own cadence. `CommunityFaqReader` is unchanged — only its injected source differs (DI factory). The `confidential/` folder is structurally excluded by reading a clean public repo. Seeded with `docs/community-kb/_seed.md`.

2. **Admin "Reload KB" button.** `IAgentPreloadCorpusBuilder.ReloadAllAsync` (`POST /Agent/Admin/ReloadKnowledgeBase`, `AdminOnly`) force-refetches the community KB and rebuilds + atomically swaps the cached preload corpus for every tier — so content updates land without an app restart. Reload+swap: fetch fresh, then overwrite cache keys (reads keep the old value until the swap). Still no TTL; restart remains a refresh, and this adds an on-demand one.
