# 40. Agent Section

## Business Context

Humans have questions about Nobodies operations that today are absorbed by coordinators or the feedback widget. The Agent answers grounded questions ("why is my consent check pending?", "what's the difference between Colaborador and Asociado?") using our own docs and the user's live state. It does NOT take actions on the user's behalf — it explains, cites, and hands off to `route_to_feedback` when it cannot answer.

## User Stories

### US-40.1 — Ask a grounded question
As a **signed-in, consented human** I want to **type a question about how the system works** so that **I get an answer grounded on our documentation and my current state, in my preferred language**.

**Acceptance:**
- Widget is visible only after consenting to `AgentChatTerms`.
- First-click without consent shows the in-page consent ceremony (reuses the `LegalController` flow).
- Response streams token-by-token within 2s of submission (SSE).
- When the docs don't cover the question, the agent calls `route_to_feedback` and returns the feedback URL rather than guessing.

### US-40.2 — See my past conversations
As a **signed-in human** I want to **review my previous agent conversations** so that **I can find an earlier answer or pick up a thread**.

**Acceptance:**
- `GET /Agent/History` lists the user's conversations with `StartedAt`, `LastMessageAt`, `MessageCount`.
- `DELETE /Agent/Conversation/{id}` deletes my own conversation; cascades to messages.

### US-40.3 — Admin reviews agent behavior
As an **Admin** I want to **see all agent conversations and refusals** so that **I can spot-check quality and feed corrections back into docs**.

**Acceptance:**
- `/Admin/Agent/Conversations` lists transcripts with filters for refusal, handoff, user.
- `/Admin/Agent/Settings` exposes `Enabled`, `Model`, caps, `PreloadConfig` (Tier1/Tier2).

### US-40.4 — Admin disables under abuse
As an **Admin** I want to **disable the agent globally with one setting change** so that **I can react to abuse or provider outages immediately**.

**Acceptance:**
- Setting `Enabled = false` hides the widget and returns 503 from `/Agent/Ask` within the next request (store refreshes on write).

## Data Model

Reference: `src/Humans.Domain/Entities/Agent*.cs`. Key entities: `AgentConversation`, `AgentMessage`, `AgentRateLimit`, `AgentSettings`. Additions to `FeedbackReport`: `Source`, `AgentConversationId`.

## Workflows

### Turn workflow
`User submits` → `rate-limit check` → `abuse check` → `prompt assembly` → `Anthropic streaming call (with cached prefix)` → `[tool loop, max 3]` → `persist messages` → `stream finalizer`.

### Handoff workflow
Tool call `route_to_feedback` → `IFeedbackService.SubmitFeedbackAsync(Category = Question, Source = AgentUnresolved, AgentConversationId = X)` → returns `FeedbackReport.Id` → agent appends transcript and terminates turn.

### Retention workflow
`AgentConversationRetentionJob` (daily 03:15 UTC) deletes `AgentConversation` rows older than `AgentSettings.RetentionDays` (default 90). Messages cascade-delete.

## Related Features

- Feedback system (`docs/features/feedback-system.md`) — handoff target.
- Legal documents (`docs/features/legal-documents.md`) — `AgentChatTerms` consent gate.
- GDPR export (`docs/features/gdpr-export.md`) — conversation/message data included.
