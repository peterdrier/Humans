<!-- freshness:triggers
  src/Sections/Humans.Agent/**
  src/Humans.Base/Services/GitHubCommunityKbContentSource.cs
  src/Humans.Base/Configuration/CommunityKbSettings.cs
  src/Sections/Humans.Backdoor/Controllers/BackdoorAgentController.cs
  src/Sections/Humans.Feedback/Domain/FeedbackReport.cs
-->
<!-- freshness:flag-on-change
  Agent conversation/message/settings invariants, preload-corpus tiers + the tool surface (fetch_section_guide / fetch_feature_spec / fetch_community_faq / route_to_issue / get_audit_history), the community knowledge base (separate nobodies-collective/knowledge-base repo, cached in RAM, admin-reloadable), rate-limit/abuse gating, and the admin status/reload/prompt-preview surface — review when agent services, stores, the tool catalog, the preload/community-KB readers, or the agent controllers change.
-->

# Agent — Section Invariants

Conversational helper backed by Anthropic Claude. Available to any authenticated, consented user when `AgentSettings.Enabled = true`.

## Concepts

- **Turn** — one user message + one streamed assistant response (may include tool calls).
- **Preload corpus** — cacheable markdown prefix containing the section *index* (one line per section: key + tagline), help glossaries, access matrix, and route map. Section invariant bodies are NOT preloaded; the model fetches them on demand via the `fetch_section_guide` tool. Help glossaries are keyed by help-widget page, so the corpus regroups them under the `fetch_section_guide` key that covers each one — the model treats any heading it sees here as a tool argument.
- **Preload config** — `Tier1` (8 highest-signal sections in the index) or `Tier2` (23 curated sections). Both fit comfortably under Anthropic ITPM caps because section bodies are routed through tool calls instead of preloaded.
<!-- NOTE: Anthropic ITPM limits count cache reads, not just fresh input. At Tier 1 (30K ITPM Sonnet), a >25K-token preload would 429 on every request because cache reads consume quota at the same rate as fresh tokens. The Tier1/Tier2 split in PreloadConfig was designed around this constraint: Tier1 caps the preload index at ~8 sections (~20K raw) to fit safely within 30K ITPM; Tier2 expands the index to 23 sections (was 14 when this note was written; `AgentPreloadCorpusBuilder.Tier2Sections` is the source of truth) once the org auto-promotes ($40 lifetime spend + 7 days elapsed => 450K ITPM Sonnet). Tier2's index lines are one-per-section taglines, not bodies, so the growth is roughly linear and still far under the 450K ITPM ceiling the tier was sized against. Admin can flip the live setting at /Agent/Admin/Settings. -->

## Data Model

### AgentConversation

**Table:** `agent_conversations`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User (cascade-delete) |
| Locale | string | User locale captured at conversation start |
| StartedAt | Instant | When the conversation started |
| LastMessageAt | Instant | Append timestamp of the most recent message |
| MessageCount | int | Cached number of messages in the conversation |

### AgentMessage

**Table:** `agent_messages`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| ConversationId | Guid | FK → AgentConversation (cascade-delete) |
| Role | AgentRole | `User`, `Assistant`, `Tool` |
| Content | string | Message text or tool result |
| FetchedDocs | string[]? | Section/feature slugs the tool dispatcher loaded for this turn |
| RefusalReason | string? | Set when the turn was refused (rate limit, abuse, disabled, etc.) |
| HandedOffToFeedbackId | Guid? | Legacy. Was populated when `route_to_feedback` auto-created a FeedbackReport. New turns leave it null — see "Issue handoff" below. Column kept for historical rows. |
| PromptTokens / OutputTokens / CachedTokens | int | Anthropic usage |
| Model | string | Model id used for the turn |
| DurationMs | int | Wall-clock duration of the turn |
| CreatedAt | Instant | Append timestamp |

### AgentSettings

**Table:** `agent_settings`

Single-row table (PK `Id = 1`, enforced by `ck_agent_settings_singleton`) holding the live tunables: `Enabled`, `Model`, `PreloadConfig` (`Tier1`/`Tier2`), `DailyMessageCap`, `HourlyMessageCap`, `DailyTokenCap`, `RetentionDays`, `UpdatedAt`. Mutated only via `IAgentSettingsService`; reads served by the Singleton `IAgentSettingsStore` (warmup hosted service preloads it). Tool-call cap is `AnthropicOptions.MaxToolCallsPerTurn` (config, not DB).

### Rate-limit counters (in-memory)

Per-user message and token counters live in the Singleton `IAgentRateLimitStore`. Phase 1 has no persisted `agent_rate_limits` table — counters reset whenever the process restarts. Phase 2 revisits persistence if abuse traffic warrants it.

### FeedbackReport additions (cross-section, legacy)

`FeedbackReport.Source` (`FeedbackSource` enum: `UserReport`, `AgentUnresolved`) and `FeedbackReport.AgentConversationId` (plain nullable Guid column, no EF FK constraint, no nav property). Owned by Feedback section. The Agent no longer writes these — historical rows produced by the original `route_to_feedback` auto-create flow remain queryable through the Feedback admin filter. Cross-section linkage was by FK column only.

## Actors & Roles

| Actor | Capability |
|---|---|
| Authenticated human | Send messages, read own history at `/Agent/Conversations`, drill into a single transcript at `/Agent/Conversation/{id}` (issue nobodies-collective/Humans#632 — own conversations only; cross-user → 404) |
| Admin | View operational status at `/Agent/Admin/Status` (usage / spend / refusals / top docs / top users / retention job / Anthropic balance), configure settings, view all conversations at `/Agent/Conversations` (Human column + filters, Older/Newer paging), drill into the diagnostic view at `/Agent/Conversations/{id}` (token counts, tool-call args, prompt preview), disable globally |
| Anyone else (anonymous) | Widget not rendered; endpoints return 401 |

## Invariants

1. **Terms link, not gate.** The Assistant panel shows a persistent "AI Terms" link below the composer that opens `/Legal/agent-chat` (the rendered Agent Chat Terms from `nobodies-collective/legal`). There is no explicit consent step — opening the panel and sending a message constitutes use; the terms describe what's sent, retention, and rights. The team-required-doc consent flow (`IConsentServiceRead.GetPendingDocumentNamesAsync`) is intentionally NOT used here; agent use is opt-in, not a membership precondition.
<!-- NOTE: Data sent to Anthropic per turn: display name, preferred locale, tier, approved flag, role assignments (names + expiry), team memberships (names only), consent pending list, open ticket IDs, open feedback IDs, open shift IDs, and conversation messages. Data NOT sent: email, phone, birthday, dietary/medical fields, payment info, profile picture, other users' personal data. Anthropic DPA: 30-day retention for abuse monitoring, no training on API inputs. GDPR export (IUserDataContributor) and retention purge (AgentConversationRetentionJob) cover the full lifecycle. -->
<!-- NOTE: Prototype validated both models on 20 curated questions (onboarding, membership tiers, teams, roles, legal/consent, tickets, shifts, governance, camps, edge cases, off-topic refusal). Graceful degradation (section not preloaded, dynamic-fetch tool not yet wired) produces 'I don't have information about that' + handoff offer — never a confident-wrong answer. All URLs spot-checked against real controllers were real. Locale (Spanish/Catalan) correct from both models. No category-level failures across 20 questions. -->
2. **Enabled gate.** If `AgentSettings.Enabled = false`, widget is hidden and `POST /Agent/Ask` returns `503 ServiceUnavailable`.
3. **Rate limit.** Per-user daily and hourly caps from `AgentSettings`. Over-cap requests return `429 TooManyRequests` without hitting the provider. A message over `AgentAskRequest.MaxMessageLength` (4000 chars; the widget’s `maxlength` is client-only) returns `400` before any other work.
4. **Tool whitelist.** Only `fetch_feature_spec`, `fetch_section_guide`, `fetch_community_faq`, `route_to_issue`, `get_audit_history`, `get_shift_details` are valid tool names (`AgentToolNames.All`). Unknown names return a tool error. `fetch_section_guide` is restricted to a whitelisted set of section keys; `fetch_feature_spec` accepts only filename-safe stems (letters, digits, `-`, `_`). Both fetch from `nobodies-collective/Humans@main` via `IGuideContentSource` and cannot read arbitrary paths. `fetch_section_guide` tries `docs/sections/{key}.md` first, falling back to `src/Sections/Humans.{key}/Docs/{key}.md` on a 404, since a section's invariants doc lives inside its own project (`AgentSectionDocReader.SectionProjectFolder`). `fetch_community_faq` resolves against topics listed by a separate `IGuideContentSource` instance (`GitHubCommunityKbContentSource`) bound to the `nobodies-collective/knowledge-base` repo instead.
5. **Tool loop bound.** At most `AnthropicOptions.MaxToolCallsPerTurn` (default 3) tool calls per turn, enforced server-side. Once the cap is exceeded mid-loop, further `tool_use` blocks in that turn get an error result telling the model to answer now instead of looking up more; the next model call then withholds tool use entirely (`ToolChoice: none`) so the model must synthesize a final answer from the results already gathered rather than the turn dead-ending on interim text (nobodies-collective/Humans#1107).
6. **Refusal logging.** Every refused turn writes an `AgentMessage` with `RefusalReason != null`.
7. **Append-only conversations per user.** A user can only post to conversations they own. `AgentController` rejects cross-user access with 404.
8. **Issue handoff is propose-only.** `route_to_issue` carries `{title, category, description}`. The dispatcher never writes a row server-side; the SSE stream emits an `issueProposal` token and the client opens the Issues submission modal pre-filled. The user reviews and submits via `/Issues/Submit`. Historical legacy auto-created `FeedbackReport.AgentConversationId` links are immutable.
<!-- route_to_issue is propose-only; do not revert to server-side auto-creation of FeedbackReport rows. -->
9. **Retention.** Conversations older than `AgentSettings.RetentionDays` are hard-deleted daily.
10. **Single provider.** One `AnthropicClient` instance, one configured model at a time. No multi-provider fallback in Phase 1.
11. **A turn never ends with an empty assistant reply.** If the tool loop (including cap-hit synthesis) produces no assistant prose, `AgentService` fills in a localized fallback before persisting instead of storing/streaming a blank bubble: for a `route_to_issue` handoff, a fallback line kept in lockstep with the widget's `Help_Agent_IssueProposed` strings (persisted only, not streamed — the widget already renders its own localized line live); otherwise a generic "couldn't answer" fallback that IS streamed to the client and logged as a warning (nobodies-collective/Humans#1144).
12. **A doc-fetch miss is recoverable, never a dead end.** `fetch_section_guide` / `fetch_feature_spec` / `fetch_community_faq` name the accepted keys in their error string so the model can correct its own call. `AgentSectionKeys` (`Humans.Agent.Contracts`) owns the accepted key set and additionally resolves the help-widget key namespace onto section keys (`Profile`/`Profiles`→`Users`, `OnboardingReview`→`Onboarding`, `LegalAndConsent`→`Consent`, `Admin`/`Board`→`Governance`, `Barrios`→`Camps`, `CityPlanning*`→`CityPlanning`, `ContainerMap`→`Containers`); every alias target must be whitelisted, and every glossary heading the preload corpus emits must resolve.
<!-- NOTE: Model default is Sonnet 4.6 (not Haiku). Prototype validated that Haiku is ~3x cheaper (~$7/mo vs ~$20/mo at 5 sessions/day x 4 turns) but Sonnet's precision and grounding matter for a support helper — both are production-viable but Sonnet is more concise and confidently grounded. The model is admin-configurable at AgentSettings.Model so the org can revisit after real usage data. -->

13. **A `max_tokens` cutoff mid tool-call JSON continues the tool loop, not a dead end.** `AnthropicClient` closes the current content block on truncation regardless of stop reason, so `AgentService` treats `stop_reason == "max_tokens"` the same as `"tool_use"` for loop continuation (nobodies-collective/Humans#963). Truncated/unparseable tool-call arguments are swapped for `{}` before the call is replayed to the provider (`ReplayableToolCalls`) — the API rejects an unmatched `tool_use` block otherwise — while the dispatcher still sees and reports the original malformed payload for the current call.
14. **A turn that throws or disconnects mid-stream never leaves an orphaned user message.** `AskAsync` drives the turn's async enumerator manually (an `await foreach` can't wrap `yield` in try/catch) so a thrown exception or an early disposal from a client disconnect both fall through to a `finally`: an assistant message with `RefusalReason = "error"` is persisted, stamped with whatever provider usage the turn accumulated before it broke, and that usage is billed through the normal rate-limit path — never a silent zero-cost failure (nobodies-collective/Humans#963, #990). A turn that reached its own finalizer (fully persisted normally) skips this fallback.

## Negative Access Rules

- Non-authenticated users never see the widget and always receive 401/403 from endpoints.
- Withdrawal of use: there is no in-app revoke button; users who want their conversation history deleted contact the Board via the email in the Terms.
- A purged conversation (retention job, or GDPR erasure of the user) is gone from member, admin, and Backdoor views alike — there is no archive.

## Tooling API — `/api/backdoor/agent`

Read-only HTTP surface for QA/prod chat-history review by dev tooling and a dev-side Claude (issue nobodies-collective/Humans#631). The controller lives in `Humans.Backdoor` and reads this section through `Humans.Agent.Contracts.IAgentTranscriptRead`; the section keeps the data and the contract, not the endpoint. One `X-Api-Key` gate for the whole machine surface, resolved to the human it was issued to.

| Endpoint | Purpose |
|----------|---------|
| `GET /api/backdoor/agent/conversations?refusalsOnly&handoffsOnly&userId&take&skip` | Conversation summaries. `take` clamped 1–200 (default 50). Each row includes `RefusalCount` (messages with `RefusalReason`), `HandoffCount` (legacy `HandedOffToFeedbackId` links plus `route_to_issue` invocations recorded in `FetchedDocs`), `LastUserMessagePreview` (200 char cap), `UserDisplayName` resolved via `IUserServiceRead.GetUserInfosAsync`. |
| `GET /api/backdoor/agent/conversations/{id}` | Full conversation envelope + ordered messages (Role, Content, CreatedAt, Model, RefusalReason, HandedOffToFeedbackId, FetchedDocs). |
| `GET /api/backdoor/agent/conversations/{id}/messages` | Messages-only view (same per-message shape). |

Missing, unknown or revoked key → 401. Unknown id → 404. Mutations (deletion, settings) stay on the admin web UI. Anything purged by `AgentConversationRetentionJob` is gone from this API too — there is no separate archive.

## Triggers

- On `route_to_issue` tool call: no server-side write. `AgentService` yields an `AgentIssueProposal` token; the client opens the Issues modal pre-filled. The user submits (or doesn't) via `/Issues/Submit` — admin triage filtering hooks into the Issues section, not Agent.
- On `AgentSettings` update: `IAgentSettingsStore` reloads the singleton; next request sees the new value.
- On user deletion: no cross-section cascade. Agent owns no FK to `users`; orphaned `agent_conversations` rows are cleaned up by `AgentConversationRetentionJob` within `RetentionDays`. `FeedbackReport.AgentConversationId` is owned by Feedback and is left as-is (the column may dangle if the conversation was purged; readers must tolerate `null` lookups).

## Cross-Section Dependencies

- **Issues** — agent handoff produces a client-side issue proposal (title/category/description) that pre-fills `/Issues/Submit`. The agent does not write Issue rows itself.
- **Feedback** — `IFeedbackServiceRead.GetOpenFeedbackIdsForUserAsync` (the Feedback section's contracts leaf) is called live by `AgentUserSnapshotProvider` to surface a user's open feedback items in the per-turn context. Additionally, historical `FeedbackReport.Source = AgentUnresolved` rows (from the original server-side handoff flow) remain readable via the Feedback admin queue; Agent no longer creates new `FeedbackReport` rows.
- **Consent (Legal)** — the Consent section's `LegalDocumentService` resolves the `agent-chat` slug to the `AgentChat/` folder in the legal repo and renders content at `/Legal/agent-chat`. The Assistant panel links there from the composer footer — a URL-only dependency; Agent injects nothing from Consent for it.
- **Users / Auth / Teams / Consent / Tickets / Shifts** — `IAgentUserSnapshotProvider` composes the per-turn user context from `IUserServiceRead.GetUserInfoAsync`, `IRoleAssignmentService.GetActiveForUserAsync`, `ITeamServiceRead.GetTeamsAsync`, `IConsentServiceRead.GetPendingDocumentNamesAsync` (surfaces pending docs in snapshot — not a gate), `ITicketServiceRead.GetUserTicketHoldingsAsync` (`OpenTicketOrderIds`), `IShiftView.GetUserAsync`, and `IBurnSettingsService.GetActiveAsync`. `IFeedbackServiceRead.GetOpenFeedbackIdsForUserAsync` is also called — see Feedback bullet above.
- **Base (inward, publishing)** — `SectionAnnotations` (`ISectionAnnotations`) publishes one "Agent doc key" annotation per `AgentSectionKeys.All` canonical key into `ISectionCatalog`, so `/Debug/Sections` shows where the assistant answers from a first-party doc and where it falls back to the community FAQ (nobodies-collective/Humans#1509). Canonical keys only, not aliases: an alias is a spelling the model uses, not a section with a doc. The key set stays a deliberate subset — operator-only sections are off it — and the catalog checks it, never derives it. A `SectionCatalogTests` case pins that every canonical key names a real section.
- **GDPR** — `AgentService` implements `IUserDataContributor` so per-user export pulls conversation history. User deletion does not cascade into Agent; orphan rows expire via the retention job.

## Architecture

**Owning services:** `AgentService` (orchestrator), `AgentSettingsService`, `AgentToolDispatcher`, `AgentUserSnapshotProvider`, `AgentAbuseDetector`, `AgentPromptAssembler`, `AgentPreloadCorpusBuilder`, `AgentPreloadAugmentor`, `AnthropicClient`. `AgentPreloadAugmentor` reads `Humans.Base`'s shared help registries (nothing Web-owned) and implements `IAgentPreloadAugmentor` from this project's `Contracts/` folder; `AgentConversationRetentionJob` lives under `Jobs/` and calls `IAgentConversationRetention`, its registration and schedule contributed via `SectionJobs.cs`.
**Owned tables:** `agent_conversations`, `agent_messages`, `agent_settings`.
**Status:** the section lives in its own project, `src/Sections/Humans.Agent`, with its cross-section surface in the project's own `Contracts/` folder. Everything except `Section`, `AgentResource`, the generated migrations, and the `Contracts/` types (`AgentSectionKeys`, `AgentRole`, `IAgentAvailability`, `IAgentConversationRetention`, `IAgentPreloadAugmentor`, `IAgentTranscriptRead` + its snapshot records) is `internal`, enforced at build time by HUM0034. Architecture tests: `tests/Humans.Agent.Tests/AgentArchitectureTests.cs`; page rendering: `tests/Humans.Integration.Tests/Controllers/AgentPageRenderTests.cs`. **No cross-section FK or nav at the EF level** — `agent_conversations.UserId`, `agent_messages.HandedOffToFeedbackId`, and `feedback_reports.AgentConversationId` are bare Guid columns.

- **DI registration** lives in `Section.Register` at the project root, discovered by Shell through `ISection`. Nothing in `Humans.Web` names the section.
- **Stores** — `IAgentSettingsStore`, `IAgentRateLimitStore` and `IAgentRetentionRunStore` are Singleton (in-process), section-internal. `AgentSettingsStoreWarmupHostedService` populates the settings store at startup; `AgentPreloadWarmupHostedService` warms the GitHub-backed preload caches after startup. Both are registered by `Section.Register`.
- **Repositories** — `IAgentRepository` (Scoped) is the single repository for the section: settings (`agent_settings`), conversations (`agent_conversations`), and messages (`agent_messages`). Nothing in the section injects `HumansDbContext` directly; `AgentRepository` injects `AgentDbContext` instead.
- **DbContext** — `AgentDbContext` (`Data/AgentDbContext.cs`, internal sealed) is the section's own per-section EF model (nobodies-collective/Humans#858 split): maps only `agent_conversations`, `agent_messages`, `agent_settings`, with its own `__EFMigrationsHistory_Agent` table and migrations under `Data/Migrations/`. Same database and connection as `HumansDbContext` — the split is a code-side partition of the EF model, not a separate database.
- **Provider boundary** — `IAnthropicClient` (Singleton, wraps the `Anthropic` 12.40.0 SDK) is the only place that touches the Anthropic API. `AgentService` knows nothing about HTTP, retries, or SDK-specific types.
- **Tooling** — `IAgentToolDispatcher` is the only path that loads section/feature markdown. `route_to_issue` does NOT call any service from the dispatcher — it returns a proposal-marker that `AgentService` rehydrates from the tool args (parsed in `ParseIssueProposalArgs`) and emits as an `AgentIssueProposal` SSE frame. The whitelist of tools is enforced in dispatcher constants; unknown names short-circuit before any I/O.
- **Authorization** — `AgentController.Ask` performs the enabled gate inline (returning 503 if disabled), then calls `IAuthorizationService.AuthorizeAsync(User, userId, [new AgentRateLimitRequirement()])` which runs `AgentRateLimitHandler` (resource-based; both types are section-internal, so there is no named policy in Shell) — the handler only checks per-user daily message cap, daily token cap, and hourly message cap. A failed authorization yields `429 TooManyRequests`. Widget visibility is controlled by `AgentSettings.Enabled`; there is no role check and no consent gate.

### Touch-and-clean guidance

- Do **not** call the Anthropic SDK directly outside `AnthropicClient`.
- Do **not** fetch `docs/sections/`, `docs/features/global/` or `src/Sections/Humans.<Section>/Docs/` markdown outside `AgentSectionDocReader` / `AgentFeatureSpecReader`; both route through the shared `IGuideContentSource` (Octokit, cached) and enforce the whitelist + filename-safe-stem validation. `AgentFeatureSpecReader` derives its servable set from the repository tree — every section `Docs/features/*.md`, plus `docs/features/global/` — so a new spec needs no registration.
- Do **not** add new tool names without updating both `AgentToolNames` and `IAgentToolDispatcher` whitelist; an unknown name must be a hard error, never a fallthrough.
- Do **not** make `route_to_issue` (or any future handoff tool) write rows server-side. Handoffs are propose-only; the user submits.
- `AgentSettings.PreloadConfig` defaults to `Tier1` (8 highest-signal sections in the index). If non-admin users start asking about sections outside that set and the model can't help, an admin can flip the live setting to `Tier2` at `/Agent/Admin/Settings` — both tiers fit Anthropic ITPM caps because section bodies route through tool calls, not preload.
