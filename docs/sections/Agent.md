# Agent — Section Invariants

Conversational helper backed by Anthropic Claude. Phase 1 is Admin-only; broader rollout follows after Phase 2.

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
| HandedOffToFeedbackId | Guid? | FK → FeedbackReport (set-null) — populated when `route_to_feedback` fires |
| PromptTokens / OutputTokens / CachedTokens | int | Anthropic usage |
| Model | string | Model id used for the turn |
| DurationMs | int | Wall-clock duration of the turn |
| CreatedAt | Instant | Append timestamp |

### AgentSettings

**Table:** `agent_settings`

Single-row table (PK `Id = 1`, enforced by `ck_agent_settings_singleton`) holding the live tunables: `Enabled`, `Model`, `PreloadConfig` (`Tier1`/`Tier2`), `DailyMessageCap`, `HourlyMessageCap`, `DailyTokenCap`, `RetentionDays`. Mutated only via `IAgentSettingsService`; reads served by the Singleton `IAgentSettingsStore` (warmup hosted service preloads it). Tool-call cap is `AnthropicOptions.MaxToolCallsPerTurn` (config, not DB).

### Rate-limit counters (in-memory)

Per-user message and token counters live in the Singleton `IAgentRateLimitStore`. Phase 1 has no persisted `agent_rate_limits` table — counters reset whenever the process restarts. Phase 2 revisits persistence if abuse traffic warrants it.

### FeedbackReport additions (cross-section)

`FeedbackReport.Source` (`FeedbackSource` enum: `UserReport`, `AgentUnresolved`) and `FeedbackReport.AgentConversationId` (plain nullable Guid column, no EF FK constraint, no nav property). Owned by Feedback section; mutated by Agent on `route_to_feedback` handoff. Cross-section linkage is by FK column only — Agent only joins to its own tables.

## Actors & Roles

| Actor | Capability |
|---|---|
| Authenticated, consented human | Send messages, read own history, delete own conversations |
| Admin | Configure settings, view all conversations, disable globally |
| Anyone else (anonymous, unconsented) | Widget not rendered; endpoints return 403 |

## Invariants

1. **Consent gate.** Widget and endpoints refuse any request from a user who has not consented to the current active `AgentChatTerms` version.
2. **Enabled gate.** If `AgentSettings.Enabled = false`, widget is hidden and `POST /Agent/Ask` returns `503 ServiceUnavailable`.
3. **Rate limit.** Per-user daily and hourly caps from `AgentSettings`. Over-cap requests return `429 TooManyRequests` without hitting the provider.
4. **Tool whitelist.** Only `fetch_feature_spec`, `fetch_section_guide`, `route_to_feedback` are valid tool names. Unknown names return a tool error; filesystem is never touched outside `docs/sections/` and `docs/features/`.
5. **Tool loop bound.** At most `AnthropicOptions.MaxToolCallsPerTurn` (default 3) tool calls per turn, enforced server-side.
6. **Refusal logging.** Every refused turn writes an `AgentMessage` with `RefusalReason != null`.
7. **Append-only conversations per user.** A user can only post to conversations they own. `AgentController` rejects cross-user access with 404.
8. **Immutable handoff link.** `FeedbackReport.AgentConversationId` is set on handoff and never changed.
9. **Retention.** Conversations older than `AgentSettings.RetentionDays` are hard-deleted daily.
10. **Single provider.** One `AnthropicClient` instance, one configured model at a time. No multi-provider fallback in Phase 1.

## Negative Access Rules

- Non-authenticated users never see the widget and always receive 401/403 from endpoints.
- A user who revokes consent (future work) loses widget visibility immediately; historical conversations are retained unless the user deletes them.
- Admin CANNOT see a conversation that belongs to a user who has deleted it.

## Triggers

- On `FeedbackReport.Source = AgentUnresolved` creation: no additional triggers — admin notification bell handles it via the existing feedback path.
- On `AgentSettings` update: `IAgentSettingsStore` reloads the singleton; next request sees the new value.
- On user deletion: no cross-section cascade. Agent owns no FK to `users`; orphaned `agent_conversations` rows are cleaned up by `AgentConversationRetentionJob` within `RetentionDays`. `FeedbackReport.AgentConversationId` is owned by Feedback and is left as-is (the column may dangle if the conversation was purged; readers must tolerate `null` lookups).

## Cross-Section Dependencies

- **Feedback** — `IFeedbackService.SubmitFromAgentAsync` writes a `FeedbackReport` with `Source = AgentUnresolved` and `AgentConversationId` set. Triage UI filters `Source = AgentUnresolved`.
- **Legal & Consent** — `ILegalDocumentSyncService` resolves the active `AgentChatTerms` version; `IConsentService` gates widget visibility.
- **Profiles / Users / Auth / Teams** — `IAgentUserSnapshotProvider` composes the per-turn user context from `IProfileService`, `IUserService`, `IRoleAssignmentService.GetActiveForUserAsync`, `ITeamService.GetActiveTeamNamesForUserAsync`.
- **GDPR** — `AgentService` implements `IUserDataContributor` so per-user export pulls conversation history. User deletion does not cascade into Agent; orphan rows expire via the retention job.

## Architecture

**Owning services:** `AgentService` (orchestrator), `AgentSettingsService`, `AgentToolDispatcher`, `AgentUserSnapshotProvider`, `AgentAbuseDetector`, `AgentPromptAssembler`, `AgentPreloadCorpusBuilder`, `AgentPreloadAugmentor`, `AnthropicClient`, `AgentConversationRetentionJob`.
**Owned tables:** `agent_conversations`, `agent_messages`, `agent_settings`.
**Status:** (B) Partially §15-migrated — `AgentService` lives in `Humans.Application/Services/Agent/` and goes through `IAgentRepository` for all DB access. `AgentSettingsService` also goes through the same `IAgentRepository` (settings + conversations + messages share one repo). Stateless helpers (`AgentPromptAssembler`, `AgentAbuseDetector`, `AgentUserSnapshotProvider`) and Infrastructure-tied services (`AgentToolDispatcher`, `AnthropicClient`) live in `Humans.Infrastructure/Services/Agent/` and `Humans.Infrastructure/Services/Anthropic/`. `AgentToolDispatcher` and `AgentUserSnapshotProvider` stay until the preload readers are abstracted. **No cross-section FK or nav at the EF level** — `agent_conversations.UserId`, `agent_messages.HandedOffToFeedbackId`, and `feedback_reports.AgentConversationId` are bare Guid columns.

- **DI registration** lives in `src/Humans.Web/Extensions/Sections/AgentSectionExtensions.cs` (`services.AddAgentSection(configuration)`), called from `InfrastructureServiceCollectionExtensions.AddHumansInfrastructure`.
- **Stores** — `IAgentSettingsStore` and `IAgentRateLimitStore` are Singleton (in-process). `AgentSettingsStoreWarmupHostedService` populates the settings store at startup.
- **Repositories** — `IAgentRepository` (Scoped) is the single repository for the section: settings (`agent_settings`), conversations (`agent_conversations`), and messages (`agent_messages`). Nothing in the section injects `HumansDbContext` directly.
- **Provider boundary** — `IAnthropicClient` (Singleton, wraps the `Anthropic` 12.11.0 SDK) is the only place that touches the Anthropic API. `AgentService` knows nothing about HTTP, retries, or SDK-specific types.
- **Tooling** — `IAgentToolDispatcher` is the only path that loads section/feature markdown or calls `IFeedbackService.SubmitFromAgentAsync`. The whitelist of tools is enforced in dispatcher constants; unknown names short-circuit before any I/O.
- **Authorization** — `AgentController.Ask` performs the consent gate and enabled gate inline (returning 403 / 503 respectively), then calls `IAuthorizationService.AuthorizeAsync(User, userId, PolicyNames.AgentRateLimit)` which runs `AgentRateLimitHandler` (resource-based) — the handler only checks per-user daily message cap, daily token cap, and hourly message cap. A failed authorization yields `429 TooManyRequests`. Phase 1 widget rendering additionally checks `User.IsInRole(RoleNames.Admin)`.

### Touch-and-clean guidance

- Do **not** call the Anthropic SDK directly outside `AnthropicClient`.
- Do **not** read `docs/sections/` or `docs/features/` outside `AgentSectionDocReader` / `AgentFeatureSpecReader`.
- Do **not** add new tool names without updating both `AgentToolNames` and `IAgentToolDispatcher` whitelist; an unknown name must be a hard error, never a fallthrough.
- Do **not** widen the Phase 1 audience beyond Admin without removing the `IsInRole(Admin)` guard in `AgentWidgetViewComponent` AND lifting the `Tier2` preload promotion (Anthropic ITPM constraint).
