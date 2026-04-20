# Agent Section Design

**Issue:** #526
**Date:** 2026-04-20
**Status:** Approved
**Section label:** `section:agent` (new)

## Overview

An in-app conversational helper that answers humans' questions about the system — "why is my consent check pending?", "what's the difference between Colaborador and Asociado?", "how do I leave a team?" — grounded on our own documentation and the signed-in user's live state. Reduces support load on coordinators and the feedback widget, and turns content we already author (section invariants, feature specs, access matrix, help glossaries) into an always-available, personalized surface.

This is a **design spike** deliverable for issue #526. It proposes architecture, provider choice, cost model, GDPR posture, guardrails, UI surface, and rollout plan. A throwaway prototype validates the approach before Phase 1 build.

**No model training required.** The approach is retrieval-augmented generation (RAG) over our own content, with prompt caching and a hybrid preload/dynamic-fetch strategy.

## Knowledge Sources

Total English corpus measured at ~517 KB ≈ ~130K tokens across:

| Source | Size | Role |
|--------|------|------|
| `docs/sections/*.md` (14 files) | ~40K tok | Invariants, actors, triggers — highest signal |
| `docs/features/*.md` (38 files) | ~90K tok | Feature spec deep dives |
| `src/Humans.Web/Models/SectionHelpContent.cs` | ~5K tok | Per-section guide + glossary markdown |
| `src/Humans.Web/Models/AccessMatrixDefinitions.cs` | ~2K tok | Role × feature access |
| User live state (per request) | ~0.5K tok | Role, team, tier, consent, open tickets |
| Route map stub | ~1K tok | Top-level URLs for "go to /Profile/Edit" answers |

Full corpus fits in a Claude Sonnet 4.6 context window (200K), so no vector store or embedding index is required. Retrieval strategy is hybrid preload + dynamic tool-fetch.

## Architecture

New vertical under `src/Humans.Web` following the "services own their data" rule. Controllers delegate to services; no cross-service DB access.

### Components

- **`AgentController`** (MVC)
  - `POST /Agent/Ask` — streams a turn response (SSE) or returns JSON. Enforces rate limits via `IAuthorizationService` resource handler.
  - `GET /Agent/History` — paginated prior conversations for the signed-in user.
  - `DELETE /Agent/Conversation/{id}` — user-initiated delete of their own conversation.
- **`IAgentService`** (owns `AgentConversation`, `AgentMessage`, `AgentRateLimit`, `AgentSettings`)
  - Prompt assembly, tool-use loop, provider call, streaming, logging.
  - Exposes `AskAsync(userId, conversationId, message, isPrivileged)`.
- **`IAgentFaqService`** (Phase 2, owns `AgentFaq`)
  - CRUD for approved FAQs; `GetTopForUser(userId, locale, budgetTokens)` returns preload-ready FAQ entries.
- **`AgentFaqPreprocessorJob`** (Phase 2, Hangfire, weekly)
  - Scans resolved `FeedbackReport` entries, extracts candidate Q/A pairs via Claude, inserts as `Status = Proposed`.
- **Admin pages**
  - `/Admin/Agent/Settings` — model, enabled toggle, rate-limit caps, preload/dynamic split tuning.
  - `/Admin/Agent/Conversations` — review transcripts; filter by refusal, handoff, user.
  - `/Admin/Agent/Faq` — Phase 2 curation UI (approve / edit / reject proposed entries).
- **UI**
  - Razor View Component at `Views/Shared/Components/AgentWidget/Default.cshtml`, rendered in `_Layout.cshtml` for signed-in users who have consented to the Agent Chat legal doc.
  - Vanilla JS at `wwwroot/js/agent/widget.js`. Floating lower-right button + slide-out chat panel, mirroring the feedback widget pattern.
  - Server-Sent Events for token-by-token streaming from `POST /Agent/Ask`. JSON fallback if SSE is disabled.

### Authorization

- Widget only rendered for signed-in users past the Agent Chat consent gate.
- Rate limits enforced by a resource-based authorization handler (`AgentRateLimitHandler`), consistent with the section's auth pattern.
- Admin pages require the `Admin` role (global superset per the admin/domain-admin rule).

## Data Model

All timestamps are NodaTime `Instant`. No concurrency tokens (per scale guidance).

```
AgentConversation
├── Id: Guid
├── UserId: Guid (FK → User)
├── StartedAt: Instant
├── LastMessageAt: Instant
├── Locale: string (5)
├── MessageCount: int
└── Navigation: User, Messages

AgentMessage
├── Id: Guid
├── ConversationId: Guid (FK → AgentConversation)
├── Role: AgentRole enum (User, Assistant, Tool)
├── Content: string (max)
├── CreatedAt: Instant
├── PromptTokens: int
├── OutputTokens: int
├── CachedTokens: int
├── Model: string (64)
├── DurationMs: int
├── FetchedDocs: string[] (JSON column) — tool-call targets
├── CitedFaqIds: Guid[] (JSON column, Phase 2)
├── RefusalReason: string? (256)
├── HandedOffToFeedbackId: Guid? (FK → FeedbackReport)
└── Navigation: Conversation, HandedOffToFeedback

AgentFaq (Phase 2)
├── Id: Guid
├── Question: string (1000)
├── Answer: string (5000)
├── Section: string (64)
├── SourceFeedbackIds: Guid[] (JSON column)
├── Locale: string (5)
├── Status: AgentFaqStatus enum (Proposed, Approved, Archived)
├── CreatedBy: Guid? (FK → User, null if LLM-proposed)
├── ApprovedBy: Guid? (FK → User)
├── ApprovedAt: Instant?
├── RefCount: int — incremented when agent cites this entry
├── CreatedAt: Instant
└── UpdatedAt: Instant

AgentRateLimit
├── UserId: Guid (FK → User) — composite PK with Day
├── Day: LocalDate
├── MessagesToday: int
└── TokensToday: int

AgentSettings (singleton row)
├── Id: int = 1
├── Enabled: bool
├── Model: string (default "claude-sonnet-4-6")
├── DailyMessageCap: int (default 30)
├── HourlyMessageCap: int (default 10)
├── DailyTokenCap: int (default 50000)
├── PreloadSectionDocs: bool (default true)
├── PreloadHelpGlossaries: bool (default true)
├── PreloadFaqBudgetTokens: int (default 25000, Phase 2)
└── UpdatedAt: Instant
```

### Enums

```
AgentRole: User, Assistant, Tool
AgentFaqStatus: Proposed, Approved, Archived
FeedbackSource: UserReport (default, existing behavior), AgentUnresolved (new)
```

### Schema Change to Existing `FeedbackReport`

Handoffs from the agent reuse the feedback system (#147) rather than introducing a parallel path. This requires one additive migration on `FeedbackReport`:

```
FeedbackReport (additions)
├── Source: FeedbackSource enum (default UserReport) — distinguishes agent handoffs
└── AgentConversationId: Guid? (FK → AgentConversation) — null for normal reports
```

When the model calls `route_to_feedback`, the service creates a `FeedbackReport` with:
- `Category = Question` (or derived from the `topic` parameter if it maps cleanly).
- `Description` = the model's summary, followed by a rendered transcript of the conversation.
- `Source = AgentUnresolved`.
- `AgentConversationId` set to the current conversation.
- `Status = Open`.

Reverse navigation: `AgentMessage.HandedOffToFeedbackId` points at the created report; `FeedbackReport.AgentConversationId` points at the source conversation. Triage UI can filter by `Source = AgentUnresolved` to surface agent failure modes for admin review.

Existing feedback reports default to `Source = UserReport` via the migration. Category and status behavior for agent-originated reports is otherwise unchanged.

## Prompt Assembly

Per turn, the prompt is constructed as:

```
[cacheable prefix — prompt caching, 5-min TTL]
  System prompt                        (~2K tokens)
  Preloaded corpus                     (~45K tokens)
    AccessMatrixDefinitions rendered
    docs/sections/*.md (all 14)
    SectionHelpContent.Glossaries (all)
    Route map stub
  Approved AgentFaqs (Phase 2)         (~25K tokens budget)
    Top-N by RefCount, locale-matched

[per-turn tail — not cached]
  User context block                   (~0.5K tokens)
    DisplayName, PreferredLocale, Tier, ApprovedFlag
    Role assignments (name + expiry)
    Team memberships
    Consent state (pending doc list)
    Open ticket IDs, open feedback IDs
  Conversation history                 (growing, soft cap ~20K)
  Latest user message
```

The cacheable prefix is invalidated only when `AgentSettings` or the corpus changes (rare). Within-session turns reuse the warm cache; cross-session cold cache is the expected path at 3–5 sessions/day.

### Tool Use

The model has three tools available. All tool names are validated against a whitelist; unknown names return an error without touching the filesystem.

| Tool | Parameters | Returns |
|------|------------|---------|
| `fetch_feature_spec` | `name: string` (whitelisted filename stem) | Markdown content of `docs/features/{name}.md` |
| `fetch_section_guide` | `section: string` (whitelisted section key) | Long procedural guide from `SectionHelpContent.Guides[section]` |
| `route_to_feedback` | `summary: string, topic: string` | Creates a `FeedbackReport` with transcript, `Source = AgentUnresolved`, and `AgentConversationId` set; returns feedback URL; terminates the turn |

Tool-use loop is bounded: maximum 3 tool calls per turn, enforced server-side. Exceeding the cap terminates the turn with a "too many lookups, try a narrower question" response.

## Guardrails

- **System prompt** enforces:
  - Answer only from provided context, preloaded corpus, fetched docs, or the user's live state.
  - Refuse off-topic (politics, personal advice, general code help, anything outside the org's operations).
  - Never fabricate URLs, role names, feature behavior, or people's names.
  - If uncertain or the corpus doesn't contain the answer, call `route_to_feedback`.
  - Respond in the user's `PreferredLocale`.
- **Refusal logging** — `RefusalReason` captured for every refused turn (off-topic, insufficient context, rate limit, abuse).
- **Rate limits** — per-user caps from `AgentSettings`. When exceeded, return a friendly message without hitting the provider. Admin can adjust caps or disable the feature entirely at `/Admin/Agent/Settings`.
- **Abuse detection** — flagged terms or patterns (self-harm, threats) route to a dedicated refusal path and notify admin via the existing notification inbox.
- **Quality review** — `/Admin/Agent/Conversations` surfaces refusals and handoffs first; admin can spot-check answers and feed corrections back into section docs or Phase 2 FAQ entries.

## Cost Model

Assumes ~5 sessions/day × ~4 turns/session = ~20 messages/day. Cache TTL (5 min default) covers within-session reuse but cold between sessions. Output ~500 tokens/turn.

| Model | Preload size | First-turn cost | Follow-up turn cost | ~Monthly cost |
|-------|--------------|-----------------|----------------------|---------------|
| Sonnet 4.6 | ~45K | $0.15 | $0.02 | **~$22** |
| Sonnet 4.6 | full ~130K | $0.49 | $0.02 | ~$73 |
| Haiku 4.5 | ~45K | $0.05 | $0.01 | **~$7** |
| Haiku 4.5 | full ~130K | $0.16 | $0.01 | ~$24 |

Rates used (January 2026): Sonnet 4.6 input $3 / Mtok, cache write 1.25× base, cache read 0.1× base; Haiku 4.5 roughly 1/3 of Sonnet. All numbers confirmed against prototype before Phase 1 build.

**Default: Sonnet 4.6 with ~45K preload.** Prototype benchmarks both models on the same 20-question test set to confirm Sonnet is worth the quality premium or flag Haiku as adequate.

## GDPR / Privacy

- **New legal doc: `DocumentKind.AgentChatTerms`.** Users consent before first use. Widget remains hidden until consent is granted. Uses existing `ConsentRecord` append-only infrastructure.
- **Data sent to Anthropic per request:**
  - User's display name, preferred locale, tier, approved flag
  - Role assignments (role names + expiry dates)
  - Team memberships (team names only)
  - Consent state (list of pending document kinds)
  - Open ticket IDs, open feedback report IDs
  - Current and prior messages in the conversation
  - Preloaded corpus and any fetched feature spec content
- **Data NOT sent:** email, phone number, birthday, dietary/medical fields, payment info, profile picture, other users' personal data.
- **Anthropic data policy** (per their DPA): 30-day retention for abuse monitoring, no training on API inputs.
- **GDPR export** (`/Profile/DataExport`) includes the user's `AgentConversation` and `AgentMessage` rows.
- **Right to deletion** cascades: `AgentConversation` and `AgentMessage` deleted with the user. `AgentFaq.SourceFeedbackIds` is anonymized (the reference is removed) but the FAQ entry itself is retained as public organizational knowledge.
- **Retention:** conversations auto-purged after 90 days by a new daily Hangfire job (`AgentConversationRetentionJob`).

## UI

- Floating lower-right button, chat icon, visible on every page after consent.
- Click opens a slide-out panel: prior message history (scrollable) + compose field.
- Streaming response renders token by token via SSE.
- Each assistant message has: markdown-rendered body, "was this helpful?" thumbs (logged as `RefCount` increment on cited FAQs), "ask a human instead" button (triggers `route_to_feedback`).
- Non-consented users see a one-time inline legal doc presentation when they click the widget for the first time.
- Accessibility: keyboard navigable, ARIA labels, focus trap in the panel, `prefers-reduced-motion` honored for the slide animation.

## Navigation

- Widget present for all signed-in, consented users across the app. No orphan pages.
- `/Admin/Agent/Settings`, `/Admin/Agent/Conversations`, `/Admin/Agent/Faq` (Phase 2) — new "Agent" group in the admin nav.

## Rollout Plan

### Phase 0 — Spike Prototype (this issue's deliverable)

- Throwaway script **outside the main app** (e.g. `tools/agent-spike/` or a notebook): paste representative corpus, call Claude API directly, run a curated set of 20 realistic user questions.
- Run the same question set against Sonnet 4.6 and Haiku 4.5; record transcripts side by side.
- Capture observations: which got right, which got confidently wrong, which required fetches, cost per question.
- Output: `docs/superpowers/specs/2026-04-20-agent-section-prototype-notes.md` with findings and a go / no-go recommendation.

### Phase 1 — Base Build

- Entities, migrations, service, controller, widget, streaming, rate limiting.
- `AgentChatTerms` legal doc.
- Admin settings + conversation review pages.
- Sonnet 4.6 only (unless spike recommends Haiku).
- Hidden behind `AgentSettings.Enabled` (default off).
- QA rollout first, then production.
- No FAQ layer yet; all retrieval is corpus + dynamic fetch.

### Phase 2 — FAQ / KB Layer

- `AgentFaq` entity + service.
- `AgentFaqPreprocessorJob` weekly.
- `/Admin/Agent/Faq` curation UI.
- Preload integration (top-N by `RefCount`, locale-matched, budgeted).
- Feedback loop closes: unresolved agent questions feed back into admin's FAQ queue.

### Phase 3 — Tuning

- Move items between preload and dynamic pools based on real usage patterns.
- Re-evaluate Sonnet vs Haiku on real conversation data.
- Consider 1-hour cache TTL if usage density rises.

## Acceptance Criteria (mapping to #526)

- [x] Architecture, retrieval strategy, provider choice, prompt scaffolding, personalization sources, guardrails, logging, UI surface, rollout plan — all in this doc.
- [ ] Cost estimate with stated assumptions — cost table above; numbers to be confirmed by prototype.
- [x] GDPR / privacy note — § GDPR / Privacy above.
- [ ] Prototype transcript (20 questions × 2 models) with notes — Phase 0 deliverable.
- [ ] Recommendation (go / no-go / later) — Phase 0 deliverable.
- [ ] Follow-up implementation issues filed on go — to be filed after spike: `agent-v1-base-build`, `agent-v2-faq-kb`, `agent-gdpr-legal-doc`.

## Out of Scope

- Model training / fine-tuning.
- Multi-turn *actions* the agent can take on the user's behalf (e.g. "join team X" via API call).
- Voice interface.
- Anonymous (non-signed-in) access.
- Multi-provider fallback (one provider at a time; switchable at settings level).

## Related

- #147 — feedback widget; natural fallback target for unresolved agent questions.
- `src/Humans.Web/Models/SectionHelpContent.cs` and `AccessMatrixDefinitions.cs` — preload corpus sources.
- `docs/sections/*.md`, `docs/features/*.md` — preload + dynamic fetch corpus.
- #522 — CityPlanning section help (adds one more source when merged).
