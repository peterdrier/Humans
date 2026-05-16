# Agent — Section Invariants

Conversational helper backed by Anthropic Claude. Available to any authenticated, consented user when `AgentSettings.Enabled = true`.

## Concepts

- **Turn** — one user message + one streamed assistant response (may include tool calls).
- **Preload corpus** — cacheable markdown prefix containing the section *index* (one line per section: key + tagline), help glossaries, access matrix, and route map. Section invariant bodies are NOT preloaded; the model fetches them on demand via the `fetch_section_guide` tool.
- **Preload config** — `Tier1` (8 highest-signal sections in the index) or `Tier2` (all 14 sections). Both fit comfortably under Anthropic ITPM caps because section bodies are routed through tool calls instead of preloaded.

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
| Authenticated human | Send messages, read own history at `/Agent/Conversations`, drill into a single transcript at `/Agent/Conversation/{id}` (issue #632 — own conversations only; cross-user → 404) |
| Admin | View operational status at `/Agent/Admin/Status` (usage / spend / refusals / top docs / top users / retention job / Anthropic balance), configure settings, view all conversations at `/Agent/Conversations` (Human column + filters), drill into the diagnostic view at `/Agent/Conversations/{id}` (token counts, tool-call args, prompt preview), disable globally |
| Anyone else (anonymous) | Widget not rendered; endpoints return 401 |

## Invariants

1. **Terms link, not gate.** The Assistant panel shows a persistent "AI Terms" link below the composer that opens `/Legal/agent-chat` (the rendered Agent Chat Terms from `nobodies-collective/legal`). There is no explicit consent step — opening the panel and sending a message constitutes use; the terms describe what's sent, retention, and rights. The team-required-doc consent flow (`IConsentService.GetPendingDocumentNamesAsync`) is intentionally NOT used here; agent use is opt-in, not a membership precondition.
2. **Enabled gate.** If `AgentSettings.Enabled = false`, widget is hidden and `POST /Agent/Ask` returns `503 ServiceUnavailable`.
3. **Rate limit.** Per-user daily and hourly caps from `AgentSettings`. Over-cap requests return `429 TooManyRequests` without hitting the provider.
4. **Tool whitelist.** Only `fetch_feature_spec`, `fetch_section_guide`, `route_to_issue`, `get_audit_history`, `get_shift_details` are valid tool names. Unknown names return a tool error; filesystem is never touched outside `docs/sections/` and `docs/features/`.
5. **Tool loop bound.** At most `AnthropicOptions.MaxToolCallsPerTurn` (default 3) tool calls per turn, enforced server-side.
6. **Refusal logging.** Every refused turn writes an `AgentMessage` with `RefusalReason != null`.
7. **Append-only conversations per user.** A user can only post to conversations they own. `AgentController` rejects cross-user access with 404.
8. **Issue handoff is propose-only.** `route_to_issue` carries `{title, category, description}`. The dispatcher never writes a row server-side; the SSE stream emits an `issueProposal` token and the client opens the Issues submission modal pre-filled. The user reviews and submits via `/Issues/Submit`. Historical legacy auto-created `FeedbackReport.AgentConversationId` links are immutable.
9. **Retention.** Conversations older than `AgentSettings.RetentionDays` are hard-deleted daily.
10. **Single provider.** One `AnthropicClient` instance, one configured model at a time. No multi-provider fallback in Phase 1.

## Negative Access Rules

- Non-authenticated users never see the widget and always receive 401/403 from endpoints.
- Withdrawal of use: there is no in-app revoke button; users who want their conversation history deleted contact the Board via the email in the Terms.
- Admin CANNOT see a conversation that belongs to a user who has deleted it.

## Tooling API — `/api/agent`

Read-only HTTP surface for QA/prod chat-history review by dev tooling and a dev-side Claude (issue #631). Mounted at `AgentApiController`, gated by `AgentApiKeyAuthFilter` against header `X-Api-Key` matching env var `AGENT_API_KEY`. Bound to its own config key so a leaked `FEEDBACK_API_KEY` or `LOG_API_KEY` cannot read agent transcripts.

| Endpoint | Purpose |
|----------|---------|
| `GET /api/agent/conversations?refusalsOnly&handoffsOnly&userId&take&skip` | Conversation summaries. `take` clamped 1–200 (default 50). Each row includes `RefusalCount`, `HandoffCount`, `LastUserMessagePreview` (200 char cap), `UserDisplayName` resolved via `IUserService.GetByIdsAsync`. |
| `GET /api/agent/conversations/{id}` | Full conversation envelope + ordered messages (Role, Content, CreatedAt, Model, RefusalReason, HandedOffToFeedbackId, FetchedDocs). |
| `GET /api/agent/conversations/{id}/messages` | Messages-only view (same per-message shape). |

Missing or wrong key → 401 (503 if the key is not configured). Unknown id → 404. Mutations (deletion, settings) stay on the admin web UI. Anything purged by `AgentConversationRetentionJob` is gone from this API too — there is no separate archive.

## Triggers

- On `route_to_issue` tool call: no server-side write. `AgentService` yields an `AgentIssueProposal` token; the client opens the Issues modal pre-filled. The user submits (or doesn't) via `/Issues/Submit` — admin triage filtering hooks into the Issues section, not Agent.
- On `AgentSettings` update: `IAgentSettingsStore` reloads the singleton; next request sees the new value.
- On user deletion: no cross-section cascade. Agent owns no FK to `users`; orphaned `agent_conversations` rows are cleaned up by `AgentConversationRetentionJob` within `RetentionDays`. `FeedbackReport.AgentConversationId` is owned by Feedback and is left as-is (the column may dangle if the conversation was purged; readers must tolerate `null` lookups).

## Cross-Section Dependencies

- **Issues** — agent handoff produces a client-side issue proposal (title/category/description) that pre-fills `/Issues/Submit`. The agent does not write Issue rows itself.
- **Feedback** — `IFeedbackService.GetOpenFeedbackIdsForUserAsync` is called live by `AgentUserSnapshotProvider` to surface a user's open feedback items in the per-turn context. Additionally, historical `FeedbackReport.Source = AgentUnresolved` rows (from the original server-side handoff flow) remain readable via the Feedback admin queue; Agent no longer creates new `FeedbackReport` rows.
- **Legal** — `LegalDocumentService` resolves the `agent-chat` slug to the `AgentChat/` folder in the legal repo and renders content at `/Legal/agent-chat`. The Assistant panel links there from the composer footer. No `IConsentService` involvement.
- **Profiles / Users / Auth / Teams / Consent / Tickets / Shifts** — `IAgentUserSnapshotProvider` composes the per-turn user context from `IProfileService`, `IUserService`, `IRoleAssignmentService.GetActiveForUserAsync`, `ITeamService.GetActiveTeamMembershipsForUserAsync`, `IConsentService.GetPendingDocumentNamesAsync` (surfaces pending docs in snapshot — not a gate), `ITicketQueryService.GetOpenTicketIdsForUserAsync`, `IShiftSignupService.GetByUserAsync`, and `IShiftManagementService.GetActiveAsync`. `IFeedbackService.GetOpenFeedbackIdsForUserAsync` is also called — see Feedback bullet above.
- **GDPR** — `AgentService` implements `IUserDataContributor` so per-user export pulls conversation history. User deletion does not cascade into Agent; orphan rows expire via the retention job.

## Architecture

**Owning services:** `AgentService` (orchestrator), `AgentSettingsService`, `AgentToolDispatcher`, `AgentUserSnapshotProvider`, `AgentAbuseDetector`, `AgentPromptAssembler`, `AgentPreloadCorpusBuilder`, `AgentPreloadAugmentor`, `AnthropicClient`, `AgentConversationRetentionJob`.
**Owned tables:** `agent_conversations`, `agent_messages`, `agent_settings`.
**Status:** (B) Partially §15-migrated — `AgentService` lives in `Humans.Application/Services/Agent/` and goes through `IAgentRepository` for all DB access. `AgentSettingsService` lives in `Humans.Infrastructure/Services/Agent/` and also goes through `IAgentRepository` (settings + conversations + messages share one repo). Stateless helpers (`AgentPromptAssembler`, `AgentAbuseDetector`, `AgentUserSnapshotProvider`, `AgentToolDispatcher`) and the Anthropic client (`AnthropicClient`) live in `Humans.Infrastructure/Services/Agent/` and `Humans.Infrastructure/Services/Anthropic/`. `AgentPreloadCorpusBuilder` lives in `Humans.Infrastructure/Services/Preload/`; `AgentPreloadAugmentor` lives in `Humans.Web/Services/Agent/`. No architecture test exists for this section yet. **No cross-section FK or nav at the EF level** — `agent_conversations.UserId`, `agent_messages.HandedOffToFeedbackId`, and `feedback_reports.AgentConversationId` are bare Guid columns.

- **DI registration** lives in `src/Humans.Web/Extensions/Sections/AgentSectionExtensions.cs` (`services.AddAgentSection(configuration)`), called from `InfrastructureServiceCollectionExtensions.AddHumansInfrastructure`.
- **Stores** — `IAgentSettingsStore` and `IAgentRateLimitStore` are Singleton (in-process). `AgentSettingsStoreWarmupHostedService` populates the settings store at startup.
- **Repositories** — `IAgentRepository` (Scoped) is the single repository for the section: settings (`agent_settings`), conversations (`agent_conversations`), and messages (`agent_messages`). Nothing in the section injects `HumansDbContext` directly.
- **Provider boundary** — `IAnthropicClient` (Singleton, wraps the `Anthropic` 12.11.0 SDK) is the only place that touches the Anthropic API. `AgentService` knows nothing about HTTP, retries, or SDK-specific types.
- **Tooling** — `IAgentToolDispatcher` is the only path that loads section/feature markdown. `route_to_issue` does NOT call any service from the dispatcher — it returns a proposal-marker that `AgentService` rehydrates from the tool args (parsed in `ParseIssueProposalArgs`) and emits as an `AgentIssueProposal` SSE frame. The whitelist of tools is enforced in dispatcher constants; unknown names short-circuit before any I/O.
- **Authorization** — `AgentController.Ask` performs the enabled gate inline (returning 503 if disabled), then calls `IAuthorizationService.AuthorizeAsync(User, userId, PolicyNames.AgentRateLimit)` which runs `AgentRateLimitHandler` (resource-based) — the handler only checks per-user daily message cap, daily token cap, and hourly message cap. A failed authorization yields `429 TooManyRequests`. Widget visibility is controlled by `AgentSettings.Enabled`; there is no role check and no consent gate.

### Touch-and-clean guidance

- Do **not** call the Anthropic SDK directly outside `AnthropicClient`.
- Do **not** read `docs/sections/` or `docs/features/` outside `AgentSectionDocReader` / `AgentFeatureSpecReader`.
- Do **not** add new tool names without updating both `AgentToolNames` and `IAgentToolDispatcher` whitelist; an unknown name must be a hard error, never a fallthrough.
- Do **not** make `route_to_issue` (or any future handoff tool) write rows server-side. Handoffs are propose-only; the user submits.
- `AgentSettings.PreloadConfig` defaults to `Tier1` (8 highest-signal sections in the index). If non-admin users start asking about sections outside that set and the model can't help, an admin can flip the live setting to `Tier2` at `/Agent/Admin/Settings` — both tiers fit Anthropic ITPM caps because section bodies route through tool calls, not preload.
