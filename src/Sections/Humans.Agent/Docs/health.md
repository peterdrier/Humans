# Agent — Target Shape

Written fresh each section-doctor run (Phase 3c), before any scan. History rows at the bottom.

## What the section does

Members ask questions in a floating help widget; a Claude-backed assistant answers them,
grounded in the org's own documentation and the member's live state (roles, teams, shifts,
tickets, consents). When it cannot answer, it drafts an issue for the member to review and
submit — it never files anything itself. Admins watch usage, spend, refusals and latency,
tune caps and the model from the shared settings page, and read any transcript.
Conversations expire on a retention clock; members' transcripts ride along in their GDPR
export and are erased with their account.

## The shapes

| Shape | Members | Notes |
|---|---|---|
| Ask a question, stream an answer | `POST /Agent/Ask` (SSE) | One question-shape: gate → ownership → rate-limit → converse with provider in a bounded tool loop → persist both sides |
| Read my own history | `/Agent/Conversations`, `/Agent/Conversation/{id}` | Own rows only; cross-user is 404 |
| Admin transcript review | `/Agent/Conversations` (admin mode), `/Agent/Conversations/{id}`, `/Agent/Admin/Conversations/{id}/Prompt` | Same list route, admin flag widens it |
| Admin operations | `/Agent/Admin/Status`, the `/Settings#agent` tab (posts to `/Agent/Admin/Settings`), `POST …/ReloadKnowledgeBase` | One status report, one settings form, one all-or-nothing cache reload |
| Machine transcript read | `IAgentTranscriptRead` (endpoint lives in Backdoor) | Same data, key-authed, read-only |
| Grounding lookups (model-facing) | Doc fetches (section / feature spec / community FAQ), live-state reads (audit history, shift details), a handoff (`route_to_issue`) | Whitelist is closed (`AgentToolNames.All`); misses name the valid keys |
| Retention | `agent-conversation-retention` job, daily | Hard delete + last-run record |
| GDPR | `IUserDataContributor` export + erase | Full transcript both ways |

## Structure

The layout those shapes imply — and the section already has, near enough:

- One controller per audience (member, admin), one machine contract consumed elsewhere; the
  settings form is a view component contributed to the Settings section's tab strip, posting
  back to the admin controller.
- `AgentService` as the single orchestrator of the turn loop; provider access behind
  `IAnthropicClient`; doc access behind the cached readers; live-state access behind
  `IAgentUserSnapshotProvider` and the tool dispatcher; prompt text in one assembler.
- Singleton in-memory stores (settings mirror, rate-limit counters, retention last-run)
  with warmup hosted services; one repository over the section's own tables.
- Preload corpus = index-only routing layer; bodies always fetched by tool. One builder, one
  augmentor collecting each section's `ISectionHelp` glossary and `ISectionAccessMatrix`
  contributions.

## Invariants

The numbered invariants in `Agent.md` are the contract; the load-bearing ones, with the line
that enforces each:

- Widget hidden when disabled (`src/Sections/Humans.Agent/Views/Shared/Components/HelpWidget/Default.cshtml:35`);
  `/Agent/Ask` answers 503 when disabled (`src/Sections/Humans.Agent/Controllers/AgentController.cs:51`).
- Over any cap is 429 before the provider (`src/Sections/Humans.Agent/Controllers/AgentController.cs:60`),
  and the service persists the refusal (`src/Sections/Humans.Agent/Services/AgentService.cs:101`).
- Every refused turn persists a message with `RefusalReason`
  (`src/Sections/Humans.Agent/Services/AgentService.cs:781`, `src/Sections/Humans.Agent/Services/AgentService.cs:809`); a failed or disconnected turn
  persists an error trace and is billed for what it consumed
  (`src/Sections/Humans.Agent/Services/AgentService.cs:227`, `src/Sections/Humans.Agent/Services/AgentService.cs:232`).
- A member reads only their own conversations; mismatch is 404
  (`src/Sections/Humans.Agent/Services/AgentService.cs:513`, `src/Sections/Humans.Agent/Controllers/AgentController.cs:123`).
- A member posts only to their own conversations; a foreign id is 403 before the stream opens
  and before anything is written (`src/Sections/Humans.Agent/Services/AgentService.cs:94`,
  `src/Sections/Humans.Agent/Controllers/AgentController.cs:83`).
- The tool whitelist is closed (`src/Sections/Humans.Agent/Services/AgentToolDispatcher.cs:29`);
  doc reads cannot reach arbitrary paths (`src/Sections/Humans.Agent/Services/Preload/AgentFeatureSpecReader.cs:116`,
  `src/Sections/Humans.Agent/Services/Preload/AgentSectionDocReader.cs:37`).
- The tool loop is bounded (`src/Sections/Humans.Agent/Services/AgentService.cs:357`); cap-hit
  forces synthesis (`src/Sections/Humans.Agent/Services/AgentService.cs:403`).
- A turn never ends with an empty assistant bubble, streamed or stored
  (`src/Sections/Humans.Agent/Services/AgentService.cs:413`).
- `route_to_issue` never writes server-side (`src/Sections/Humans.Agent/Services/AgentToolDispatcher.cs:84`).
- An incomplete preload corpus or community index is served but never cached, and a reload
  publishes nothing unless every fetch succeeded
  (`src/Sections/Humans.Agent/Services/Preload/AgentPreloadCorpusBuilder.cs:44`,
  `src/Sections/Humans.Agent/Services/Preload/AgentPreloadCorpusBuilder.cs:57`).

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
- No standalone settings page — `Views/Admin/Agent/Settings.cshtml` renders only when the
  settings POST fails model binding; the GET lives on `/Settings#agent`.

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
- **Streamed and persisted replies read `AgentResource` through a `ResourceManager`**
  (`LocalizedReply`), not `IStringLocalizer`: the culture is the conversation's stored locale,
  not the request's. The route_to_issue fallback is persisted only, and uses the same
  `Help_Agent_IssueProposed` key the widget renders live.
- **`AgentDocsHealthCheck` bypasses the cached readers** so the probe genuinely re-tests GitHub
  each call; its section canary is this section's own `Agent.md`, which moves only with the
  section.
- **Retention job logs at Warning** on deletion so the entry shows in the prod log viewer
  (Warning+ only).
- **`SectionAnnotations` publishes canonical keys only** — aliases are spellings, not sections.
- **`AgentService.RunTurnAsync` and `AnthropicClient.StreamAsync` are big by design** — the
  load-bearing streaming loops (manual enumerator for error-path billing; SSE block
  assembly). Their reforge complexity scores are examined and accepted; don't refactor them
  for score.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-28 | First doctoring: doc drift + narration purge, duplicate view record collapsed | peterdrier/Humans#1553 |
| section-doctor | 2026-10-03 | Docs health check probes a doc that exists; section-guide reader drops its dead first fetch | peterdrier/Humans#1893 |
