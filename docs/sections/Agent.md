# Agent — Section Invariants

Conversational helper backed by Anthropic Claude. Phase 1 is Admin-only; broader rollout follows after Phase 2.

## Concepts

- **Turn** — one user message + one streamed assistant response (may include tool calls).
- **Preload corpus** — cacheable markdown prefix including section invariants, help glossaries, access matrix, route map.
- **Preload config** — `Tier1` (~25K tokens, safe for Anthropic Tier-1 ITPM) or `Tier2` (~45K tokens, full coverage, requires promoted org).

## Data Model

### AgentConversation

**Table:** `agent_conversations`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User (cascade-delete) |
| Title | string | First-message-derived label |
| CreatedAt | Instant | When the conversation started |
| UpdatedAt | Instant | Last message append |

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
| InputTokens | int? | Anthropic usage |
| OutputTokens | int? | Anthropic usage |
| CreatedAt | Instant | Append timestamp |

### AgentRateLimit

**Table:** `agent_rate_limits`

| Property | Type | Purpose |
|----------|------|---------|
| UserId | Guid | PK — one row per user |
| HourlyCount | int | Turns in the current hour bucket |
| HourlyResetAt | Instant | Bucket boundary |
| DailyCount | int | Turns in the current day bucket |
| DailyResetAt | Instant | Bucket boundary |

### AgentSettings

**Table:** `agent_settings`

Single-row table holding the live tunables: `Enabled`, `PreloadConfig` (`Tier1`/`Tier2`), `MaxToolCallsPerTurn`, `RetentionDays`, per-user `HourlyCap`, `DailyCap`. Mutated only via `IAgentSettingsService`; reads served by the Singleton `IAgentSettingsStore` (warmup hosted service preloads it).

### FeedbackReport additions (cross-section)

`FeedbackReport.Source` (`FeedbackSource` enum: `User`, `AgentUnresolved`) and `FeedbackReport.AgentConversationId` (nullable FK → AgentConversation, set-null on deletion). Owned by Feedback section; mutated by Agent on `route_to_feedback` handoff.

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
- On user deletion: `AgentConversation` cascades → `AgentMessage` cascades. `FeedbackReport.AgentConversationId` is set-null (report survives).

## Cross-Section Dependencies

- **Feedback** — `IFeedbackService.SubmitFromAgentAsync` writes a `FeedbackReport` with `Source = AgentUnresolved` and `AgentConversationId` set. Triage UI filters `Source = AgentUnresolved`.
- **Legal & Consent** — `ILegalDocumentSyncService` resolves the active `AgentChatTerms` version; `IConsentService` gates widget visibility.
- **Profiles / Users / Auth / Teams** — `IAgentUserSnapshotProvider` composes the per-turn user context from `IProfileService`, `IUserService`, `IRoleAssignmentService.GetActiveForUserAsync`, `ITeamService.GetActiveTeamNamesForUserAsync`.
- **GDPR** — `AgentService` implements `IUserDataContributor` so per-user export pulls conversation history; user deletion cascades `AgentConversation` → `AgentMessage`.

## Architecture

**Owning services:** `AgentService` (orchestrator), `AgentSettingsService`, `AgentToolDispatcher`, `AgentUserSnapshotProvider`, `AgentAbuseDetector`, `AgentPromptAssembler`, `AgentPreloadCorpusBuilder`, `AgentPreloadAugmentor`, `AnthropicClient`, `AgentConversationRetentionJob`.
**Owned tables:** `agent_conversations`, `agent_messages`, `agent_rate_limits`, `agent_settings`.
**Status:** (B) Section-organized but not §15-migrated — services live in `Humans.Infrastructure/Services/Agent/` and `Humans.Infrastructure/Services/Preload/`, repositories live in `Humans.Infrastructure/Repositories/`. Phase 1 ships in this shape; a future pass moves the orchestrator and stateless helpers to `Humans.Application/Services/Agent/` once §15 work in adjacent sections settles.

- **DI registration** lives in `src/Humans.Web/Extensions/Sections/AgentSectionExtensions.cs` (`services.AddAgentSection(configuration)`), called from `InfrastructureServiceCollectionExtensions.AddHumansInfrastructure`.
- **Stores** — `IAgentSettingsStore` and `IAgentRateLimitStore` are Singleton (in-process). `AgentSettingsStoreWarmupHostedService` populates the settings store at startup.
- **Repositories** — `IAgentConversationRepository` (Scoped) handles persistence of conversations and messages.
- **Provider boundary** — `IAnthropicClient` (Singleton, wraps the `Anthropic` 12.11.0 SDK) is the only place that touches the Anthropic API. `AgentService` knows nothing about HTTP, retries, or SDK-specific types.
- **Tooling** — `IAgentToolDispatcher` is the only path that loads section/feature markdown or calls `IFeedbackService.SubmitFromAgentAsync`. The whitelist of tools is enforced in dispatcher constants; unknown names short-circuit before any I/O.
- **Authorization** — `[Authorize(Policy = AgentPolicyNames.AgentAsk)]` on `AgentController.Ask` runs `AgentRateLimitHandler` (resource-based requirement) which checks consent + enabled + per-user caps before the controller body. Phase 1 widget rendering additionally checks `User.IsInRole(RoleNames.Admin)`.

### Touch-and-clean guidance

- Do **not** call the Anthropic SDK directly outside `AnthropicClient`.
- Do **not** read `docs/sections/` or `docs/features/` outside `AgentSectionDocReader` / `AgentFeatureSpecReader`.
- Do **not** add new tool names without updating both `AgentToolNames` and `IAgentToolDispatcher` whitelist; an unknown name must be a hard error, never a fallthrough.
- Do **not** widen the Phase 1 audience beyond Admin without removing the `IsInRole(Admin)` guard in `AgentWidgetViewComponent` AND lifting the `Tier2` preload promotion (Anthropic ITPM constraint).
