# Agent — Target Shape

Written fresh each section-doctor run (Phase 3c), before any scan. History rows at the bottom.

## What the section does

Members ask questions in a floating help widget; a Claude-backed assistant answers them,
grounded in the org's own documentation and the member's live state (roles, teams, shifts,
tickets, consents). When it cannot answer, it drafts an issue for the member to review and
submit — it never files anything itself. Admins can watch usage, spend, refusals and latency,
tune caps and the model, and read any transcript. Conversations expire on a retention clock;
members' transcripts ride along in their GDPR export and are erased with their account.

## The shapes

| Shape | Members | Notes |
|---|---|---|
| Ask a question, stream an answer | `POST /Agent/Ask` (SSE) | One question-shape: gate → rate-limit → converse with provider in a bounded tool loop → persist both sides |
| Read my own history | `/Agent/Conversations`, `/Agent/Conversation/{id}` | Own rows only; cross-user is 404 |
| Admin transcript review | `/Agent/Conversations` (admin mode), `/Agent/Conversations/{id}`, `/Agent/Admin/Conversations/{id}/Prompt` | Same list route, admin flag widens it |
| Admin operations | `/Agent/Admin/Status`, `/Agent/Admin/Settings`, `POST …/ReloadKnowledgeBase` | One status report, one settings form, one cache reload |
| Machine transcript read | `IAgentTranscriptRead` (endpoint lives in Backdoor) | Same data, key-authed, read-only |
| Grounding lookups (model-facing) | Doc fetches (section / feature spec / community FAQ), live-state reads (audit history, shift details), a handoff (`route_to_issue`) | Whitelist is closed (`AgentToolNames.All`); misses name the valid keys |
| Retention | `agent-conversation-retention` job, daily | Hard delete + last-run record |
| GDPR | `IUserDataContributor` export + erase | Full transcript both ways |

## Structure

The layout those shapes imply — and the section already has, near enough:

- One controller per audience (member, admin), one machine contract consumed elsewhere.
- `AgentService` as the single orchestrator of the turn loop; provider access behind
  `IAnthropicClient`; doc access behind the cached readers; live-state access behind
  `IAgentUserSnapshotProvider` and the tool dispatcher; prompt text in one assembler.
- Singleton in-memory stores (settings mirror, rate-limit counters, retention last-run)
  with warmup hosted services; one repository over the section's own tables.
- Preload corpus = index-only routing layer; bodies always fetched by tool. One builder, one
  augmentor for Base-owned help content.

## Invariants

The numbered invariants in `Agent.md` are the contract; the load-bearing ones restated:

- Every refused turn persists a message with `RefusalReason`; every failed/disconnected turn
  persists an error trace and is billed for what it consumed — no silent zero-cost failures.
- A user can only post to / read conversations they own; mismatch is 404, never 403.
- The tool whitelist is closed; doc reads cannot reach arbitrary paths; a key miss names the
  accepted keys.
- The tool loop is bounded (`MaxToolCallsPerTurn`); cap-hit forces synthesis, never a dead end.
- A turn never ends with an empty assistant bubble, streamed or stored.
- `route_to_issue` never writes server-side.
- Widget hidden + `/Agent/Ask` 503 when disabled; 429 over caps, checked before the provider.

## Seams

- **Rate-limit persistence (Phase 2).** Counters are in-memory by design; a persisted
  `agent_rate_limits` table is reserved space, built only if abuse traffic warrants it.
- **Legacy handoff columns.** `HandedOffToFeedbackId` and Feedback's `AgentConversationId`
  exist only for historical rows from the superseded auto-create flow; readers tolerate them,
  nothing new writes them.

## Deliberately not done

- No multi-provider fallback; one `AnthropicClient`, one configured model.
- No consent gate on use — terms link, not gate; the team-required-doc consent flow is
  intentionally not used.
- No named authorization policy for rate limiting — the requirement is instantiated at its one
  call site (Store/Expenses/Containers shape).
- No `AgentFaq` table/service — the community-KB repo + `fetch_community_faq` replaced that
  design entirely.
- No per-file keyword extraction in-app — the KB generator pipeline owns keyword quality.
- No `ExecuteDeleteAsync` in purge paths — load+remove keeps the in-memory test provider viable
  at this scale.

## Load-bearing weirdness

- **`AskAsync` drives its inner enumerator manually.** C# forbids `yield` inside try/catch;
  the manual loop is what lets a thrown turn or client disconnect still persist a billed error
  trace. The `finally` also catches disposal-without-exception (disconnect at a `yield return`).
- **Caps are checked twice** — resource-based authorization in the controller (429 before SSE
  starts) and again inside `AskAsync` (persists the refusal per invariant 6). Both matter: the
  handler can't persist, the service can't set a status code.
- **`max_tokens` continues the tool loop** and truncated tool-call JSON is replayed as `{}`
  (`ReplayableToolCalls`) while the dispatcher still sees the raw payload — API rejects
  unmatched `tool_use` blocks otherwise.
- **`AgentRepository.AppendMessageAsync` clears the change tracker on failure** so the
  follow-up error-trace append doesn't flush the half-written first append.
- **Preload is index-only by ITPM design.** Tier1/Tier2 exist because Anthropic ITPM counts
  cache reads; section bodies route through tools so both tiers fit the caps.
- **The section fetches its own repo's docs from GitHub at runtime** (not from disk): the
  deployed app has no source tree, and the KB lives in a separate repo. Caches are
  `NeverRemove`; the admin reload refreshes the community KB and rebuilds the assembled
  corpus, while the section-guide and feature-spec reader caches refresh only on restart
  (the rebuilt corpus re-reads section taglines through those still-warm caches).
- **Localized fallback strings live in C#, not resx** (`SilentTurnFallbackText`,
  `RouteToIssueFallbackText`): they are both streamed and persisted, and the service layer has
  no resx access. The route_to_issue one is deliberately persisted-only, in lockstep with the
  widget's `Help_Agent_IssueProposed` strings.
- **`AgentDocsHealthCheck` bypasses the cached readers** and probes canaries that don't move
  (`docs/sections/_Index.md`) so the probe genuinely re-tests GitHub each call.
- **Retention job logs at Warning** on deletion so the entry shows in the prod log viewer
  (Warning+ only).
- **`SectionAnnotations` publishes canonical keys only** — aliases are spellings, not sections.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-28 | First doctoring: doc drift + narration purge, duplicate view record collapsed | peterdrier/Humans#1553 |
