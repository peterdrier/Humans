# Agent — Section Invariants

## Concepts

- **Turn** — one user message + one streamed assistant response (may include tool calls).
- **Preload corpus** — cacheable markdown prefix including section invariants, help glossaries, access matrix, route map.
- **Preload config** — `Tier1` (~25K tokens, safe for Anthropic Tier-1 ITPM) or `Tier2` (~45K tokens, full coverage, requires promoted org).

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

## Negative access rules

- Non-authenticated users never see the widget and always receive 401/403 from endpoints.
- A user who revokes consent (future work) loses widget visibility immediately; historical conversations are retained unless the user deletes them.
- Admin CANNOT see a conversation that belongs to a user who has deleted it.

## Triggers

- On `FeedbackReport.Source = AgentUnresolved` creation: no additional triggers — admin notification bell handles it via the existing feedback path.
- On `AgentSettings` update: `IAgentSettingsStore` reloads the singleton; next request sees the new value.
- On user deletion: `AgentConversation` cascades → `AgentMessage` cascades. `FeedbackReport.AgentConversationId` is set-null (report survives).

## Cross-Section Dependencies

- **Feedback** — `route_to_feedback` tool creates a `FeedbackReport`. Triage UI filters `Source = AgentUnresolved`.
- **Legal & Consent** — `AgentChatTerms` document gate.
- **GDPR** — export + deletion wired via `IUserDataContributor` and cascade rules.
- **Admin** — settings + review pages under `/Admin/Agent/*`.
